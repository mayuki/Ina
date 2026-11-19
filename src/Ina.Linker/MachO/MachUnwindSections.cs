using Ina.Linker.Elf;

namespace Ina.Linker.MachO;

/// <summary>
/// The output <c>__eh_frame</c>: the FDEs of the remaining code whose compact unwind encoding defers to DWARF,
/// each preceded by its CIE where needed. The fields that hold addresses are rewritten for the new positions.
/// </summary>
internal sealed class MachEhFrameSection : MachOutputSection
{
    private readonly List<EhFrameCie> _cies = [];
    private readonly List<EhFrameFde> _fdes = [];

    public MachEhFrameSection()
        : base(
            "__TEXT", "__eh_frame",
            MachSectionType.Coalesced | MachSectionType.AttrNoToc | MachSectionType.AttrStripStaticSyms | MachSectionType.AttrLiveSupport)
    {
        Alignment = 8;
    }

    /// <summary>
    /// Places the records. Their positions only depend on the order of the code, which is why this can happen
    /// before the section itself has an address: the unwind table in front of it needs the offsets of the FDEs.
    /// </summary>
    public void Place(IEnumerable<UnwindRecord> recordsInAddressOrder)
    {
        var offset = 0L;
        foreach (var record in recordsInAddressOrder)
        {
            if (record.Fde is not { } fde || fde.OutputOffset >= 0)
            {
                continue;
            }

            if (fde.Cie.OutputOffset < 0)
            {
                fde.Cie.OutputOffset = offset;
                offset += fde.Cie.Length;
                _cies.Add(fde.Cie);
            }

            fde.OutputOffset = offset;
            offset += fde.Length;
            _fdes.Add(fde);
        }

        Size = (ulong)offset;
    }

    public override void Finish(MachLinkContext context)
    {
    }

    public override void Write(MachLinkContext context, Span<byte> data)
    {
        foreach (var cie in _cies)
        {
            var record = data.Slice((int)cie.OutputOffset, cie.Length);
            cie.Section.Content.Span.Slice(cie.Offset, cie.Length).CopyTo(record);
            if (cie.PersonalityFieldOffset >= 0)
            {
                var personality = cie.Section.File.Symbols[cie.PersonalityIndex];
                var field = cie.PersonalityFieldOffset - cie.Offset;
                WritePcRelative(record, field, cie.PersonalityEncoding, context.GotAddress(personality), Address + (ulong)cie.OutputOffset + (ulong)field, cie.Section);
            }
        }

        foreach (var fde in _fdes)
        {
            var record = data.Slice((int)fde.OutputOffset, fde.Length);
            fde.Section.Content.Span.Slice(fde.Offset, fde.Length).CopyTo(record);
            var recordAddress = Address + (ulong)fde.OutputOffset;

            // The CIE is named by its distance from this field.
            LittleEndian.U32(record, 4, (uint)(fde.OutputOffset + 4 - fde.Cie.OutputOffset));

            var pcBegin = fde.PcBeginFieldOffset - fde.Offset;
            WritePcRelative(record, pcBegin, fde.Cie.PointerEncoding, fde.Function.Address + fde.FunctionOffset, recordAddress + (ulong)pcBegin, fde.Section);

            if (fde.Lsda is { } lsda)
            {
                var field = fde.LsdaFieldOffset - fde.Offset;
                WritePcRelative(record, field, fde.Cie.LsdaEncoding, lsda.Address + fde.LsdaOffset, recordAddress + (ulong)field, fde.Section);
            }
        }
    }

    private static void WritePcRelative(Span<byte> record, int field, byte encoding, ulong target, ulong fieldAddress, MachInputSection origin)
    {
        var distance = (long)(target - fieldAddress);
        if (EhFrameEncoding.Size(encoding) == 8)
        {
            LittleEndian.I64(record, field, distance);
        }
        else if (distance is >= int.MinValue and <= int.MaxValue)
        {
            LittleEndian.I32(record, field, (int)distance);
        }
        else
        {
            throw new LinkerException($"{origin.DisplayName}: unwind information is too far away from the code it describes");
        }
    }
}

/// <summary>
/// The compact unwind table <c>__unwind_info</c>, which the unwinder searches by address: a first-level index over
/// pages of function entries, an index of the language specific data, and the personality functions.
/// </summary>
internal sealed class MachUnwindInfoSection : MachOutputSection
{
    private const int HeaderSize = 28;
    private const int IndexEntrySize = 12;
    private const int LsdaEntrySize = 8;
    private const int PageHeaderSize = 12;
    private const int PageSize = 4096;
    private const int MaxCommonEncodings = 127;
    private const int MaxPersonalities = 3;

    private readonly List<UnwindRecord> _records;
    private readonly IReadOnlyList<MachOutputSection> _codeSections;
    private readonly MachEhFrameSection _ehFrame;

    private readonly List<Entry> _entries = [];
    private readonly List<MachSymbol> _personalities = [];
    private readonly List<uint> _commonEncodings = [];
    private readonly List<Page> _pages = [];
    private int _lsdaCount;
    private ulong _endAddress;

    /// <param name="records">The unwind records of all atoms that are part of the output.</param>
    /// <param name="codeSections">The output sections that contain code, which is what the table covers.</param>
    public MachUnwindInfoSection(List<UnwindRecord> records, IReadOnlyList<MachOutputSection> codeSections, MachEhFrameSection ehFrame)
        : base("__TEXT", "__unwind_info", MachSectionType.Regular)
    {
        _records = records;
        _codeSections = codeSections;
        _ehFrame = ehFrame;
        Alignment = 4;
    }

    private readonly record struct Entry(ulong Address, uint Encoding, UnwindRecord? Record)
    {
        public bool HasLsda => (Encoding & CompactUnwind.HasLsda) != 0 && Record?.Lsda is not null;
    }

    private sealed record Page(int First, int Count, List<uint> Encodings)
    {
        public int Size => PageHeaderSize + Count * 4 + Encodings.Count * 4;
    }

    public override void Finish(MachLinkContext context)
    {
        // The code has its final addresses by now, because this section comes after it.
        _records.Sort(static (a, b) => a.Address.CompareTo(b.Address));
        _ehFrame.Place(_records);

        CollectEntries(context);
        ChooseCommonEncodings();
        BuildPages();

        Size = (ulong)(HeaderSize + _commonEncodings.Count * 4 + _personalities.Count * 4 + (_pages.Count + 1) * IndexEntrySize
            + _lsdaCount * LsdaEntrySize + _pages.Sum(static p => p.Size));
    }

    public override void Write(MachLinkContext context, Span<byte> data)
    {
        var imageBase = context.ImageBase;
        var commonOffset = HeaderSize;
        var personalityOffset = commonOffset + _commonEncodings.Count * 4;
        var indexOffset = personalityOffset + _personalities.Count * 4;
        var lsdaOffset = indexOffset + (_pages.Count + 1) * IndexEntrySize;
        var pageOffset = lsdaOffset + _lsdaCount * LsdaEntrySize;

        LittleEndian.U32(data, 0, 1); // version
        LittleEndian.U32(data, 4, (uint)commonOffset);
        LittleEndian.U32(data, 8, (uint)_commonEncodings.Count);
        LittleEndian.U32(data, 12, (uint)personalityOffset);
        LittleEndian.U32(data, 16, (uint)_personalities.Count);
        LittleEndian.U32(data, 20, (uint)indexOffset);
        LittleEndian.U32(data, 24, (uint)(_pages.Count + 1));

        for (var i = 0; i < _commonEncodings.Count; i++)
        {
            LittleEndian.U32(data, commonOffset + i * 4, _commonEncodings[i]);
        }

        // A personality is given as the offset of the GOT entry that holds its address.
        for (var i = 0; i < _personalities.Count; i++)
        {
            LittleEndian.U32(data, personalityOffset + i * 4, (uint)(context.GotAddress(_personalities[i]) - imageBase));
        }

        var commonIndex = new Dictionary<uint, int>();
        for (var i = 0; i < _commonEncodings.Count; i++)
        {
            commonIndex[_commonEncodings[i]] = i;
        }

        var lsdaPosition = lsdaOffset;
        for (var p = 0; p < _pages.Count; p++)
        {
            var page = _pages[p];
            var first = _entries[page.First];
            var index = data.Slice(indexOffset + p * IndexEntrySize, IndexEntrySize);
            LittleEndian.U32(index, 0, (uint)(first.Address - imageBase));
            LittleEndian.U32(index, 4, (uint)pageOffset);
            LittleEndian.U32(index, 8, (uint)lsdaPosition);

            var header = data.Slice(pageOffset, page.Size);
            LittleEndian.U32(header, 0, CompactUnwind.SecondLevelCompressed);
            LittleEndian.U16(header, 4, PageHeaderSize);
            LittleEndian.U16(header, 6, (ushort)page.Count);
            LittleEndian.U16(header, 8, (ushort)(PageHeaderSize + page.Count * 4));
            LittleEndian.U16(header, 10, (ushort)page.Encodings.Count);

            for (var i = 0; i < page.Count; i++)
            {
                var entry = _entries[page.First + i];

                // An entry is the distance from the first function of the page and the index of its encoding, which
                // is either one of the common encodings or one that is local to the page.
                var encodingIndex = commonIndex.TryGetValue(entry.Encoding, out var common)
                    ? common
                    : _commonEncodings.Count + page.Encodings.IndexOf(entry.Encoding);
                LittleEndian.U32(header, PageHeaderSize + i * 4, (uint)(entry.Address - first.Address) | ((uint)encodingIndex << 24));

                if (entry.HasLsda)
                {
                    var lsda = entry.Record!.Lsda!;
                    LittleEndian.U32(data, lsdaPosition, (uint)(entry.Address - imageBase));
                    LittleEndian.U32(data, lsdaPosition + 4, (uint)(lsda.Address + entry.Record.LsdaOffset - imageBase));
                    lsdaPosition += LsdaEntrySize;
                }
            }

            for (var i = 0; i < page.Encodings.Count; i++)
            {
                LittleEndian.U32(header, PageHeaderSize + page.Count * 4 + i * 4, page.Encodings[i]);
            }

            pageOffset += page.Size;
        }

        // The index ends with an entry for the address behind the last function; it has no page.
        var sentinel = data.Slice(indexOffset + _pages.Count * IndexEntrySize, IndexEntrySize);
        LittleEndian.U32(sentinel, 0, (uint)(_endAddress - imageBase));
        LittleEndian.U32(sentinel, 8, (uint)lsdaPosition);
    }

    /// <summary>Turns the records into table entries in address order, with their final encodings.</summary>
    private void CollectEntries(MachLinkContext context)
    {
        var dwarfMode = context.Target.DwarfUnwindMode;
        var entries = new List<Entry>(_records.Count);
        foreach (var record in _records)
        {
            uint encoding;
            if (record.Fde is { } fde)
            {
                // The low bits tell the unwinder where the FDE is, so that it does not have to search for it.
                if (fde.OutputOffset > CompactUnwind.DwarfSectionOffsetMask)
                {
                    throw new LinkerException("__eh_frame is too large for the compact unwind table to refer to its entries");
                }

                encoding = dwarfMode | (uint)fde.OutputOffset;
            }
            else
            {
                encoding = record.Encoding & ~CompactUnwind.PersonalityMask;
                if (record.Lsda is not null)
                {
                    encoding |= CompactUnwind.HasLsda;
                }

                if (record.PersonalityIndex >= 0)
                {
                    encoding |= (uint)PersonalityNumber(record.Function.File.Symbols[record.PersonalityIndex]) << CompactUnwind.PersonalityShift;
                }
            }

            entries.Add(new Entry(record.Address, encoding, record));
        }

        // Code without unwind information gets an explicit "nothing known" entry. Otherwise a lookup would find
        // the entry of the function in front of it and unwind with the wrong rules.
        _endAddress = 0;
        foreach (var section in _codeSections)
        {
            foreach (var atom in section.Atoms)
            {
                if (atom.Size > 0 && atom.UnwindRecords is not { Count: > 0 })
                {
                    entries.Add(new Entry(atom.Address, 0, null));
                }
            }

            _endAddress = Math.Max(_endAddress, section.Address + section.Size);
        }

        entries.Sort(static (a, b) => a.Address.CompareTo(b.Address));
        if (_records.Count > 0)
        {
            var last = _records[^1];
            _endAddress = Math.Max(_endAddress, last.Address + last.Length);
        }

        // Neighbours with the same encoding and no data of their own are one entry as far as lookups are concerned.
        _entries.Clear();
        _lsdaCount = 0;
        foreach (var entry in entries)
        {
            if (_entries.Count > 0 && _entries[^1].Encoding == entry.Encoding && (entry.Encoding & CompactUnwind.HasLsda) == 0
                && (entry.Encoding & CompactUnwind.ModeMask) != dwarfMode)
            {
                continue;
            }

            _entries.Add(entry);
            if (entry.HasLsda)
            {
                _lsdaCount++;
            }
        }
    }

    private int PersonalityNumber(MachSymbol personality)
    {
        var index = _personalities.IndexOf(personality);
        if (index < 0)
        {
            if (_personalities.Count == MaxPersonalities)
            {
                throw new LinkerException(
                    $"too many personality functions: the unwind table can refer to {MaxPersonalities} ('{personality.Name}' is one too many)");
            }

            index = _personalities.Count;
            _personalities.Add(personality);
        }

        return index + 1;
    }

    /// <summary>The most frequent encodings are stored once in the header and shared by all pages.</summary>
    private void ChooseCommonEncodings()
    {
        var frequency = new Dictionary<uint, int>();
        foreach (var entry in _entries)
        {
            frequency[entry.Encoding] = frequency.GetValueOrDefault(entry.Encoding) + 1;
        }

        _commonEncodings.Clear();
        _commonEncodings.AddRange(frequency
            .Where(static pair => pair.Value > 1)
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key)
            .Take(MaxCommonEncodings)
            .Select(static pair => pair.Key));
    }

    /// <summary>
    /// Fills pages greedily. An entry of a page stores its distance from the first one in 24 bits and the index of
    /// its encoding in 8 bits, which limits what fits on a page besides the page size.
    /// </summary>
    private void BuildPages()
    {
        var common = new HashSet<uint>(_commonEncodings);
        _pages.Clear();
        for (var first = 0; first < _entries.Count;)
        {
            var local = new List<uint>();
            var size = PageHeaderSize;
            var count = 0;
            while (first + count < _entries.Count)
            {
                var entry = _entries[first + count];
                if (entry.Address - _entries[first].Address >= 1 << 24)
                {
                    break;
                }

                var isNew = !common.Contains(entry.Encoding) && !local.Contains(entry.Encoding);
                var grown = size + 4 + (isNew ? 4 : 0);
                if (grown > PageSize || (isNew && _commonEncodings.Count + local.Count >= 256))
                {
                    break;
                }

                if (isNew)
                {
                    local.Add(entry.Encoding);
                }

                size = grown;
                count++;
            }

            _pages.Add(new Page(first, count, local));
            first += count;
        }
    }
}
