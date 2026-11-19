using System.Text;
using Ina.Linker.Elf;

namespace Ina.Linker.MachO;

/// <summary>The value of a pointer-sized field of an unwind table, evaluated inside the object file.</summary>
/// <param name="Value">The content of the field with all relocations against symbols of this object applied.</param>
/// <param name="ExternalSymbol">Index of a symbol that this object does not define and that the field refers to, or -1.</param>
/// <param name="IsGotReference">Whether the field refers to the GOT entry of <paramref name="ExternalSymbol"/>.</param>
/// <param name="HasRelocation">Whether any relocation applies to the field.</param>
internal readonly record struct MachFieldValue(long Value, int ExternalSymbol, bool IsGotReference, bool HasRelocation);

/// <summary>A relocatable 64-bit Mach-O object file (<c>MH_OBJECT</c>).</summary>
internal sealed class MachObjectFile
{
    private const int RelocationSize = 8;

    private readonly List<MachNlist> _nlist = [];
    private readonly Dictionary<Atom, int> _anchors = [];
    private readonly Dictionary<MachSymbol, int> _syntheticSymbols = [];

    private MachObjectFile(string name, uint cpuType)
    {
        Name = name;
        CpuType = cpuType;
    }

    /// <summary>Display name, e.g. <c>libfoo.a(bar.o)</c>.</summary>
    public string Name { get; }

    public uint CpuType { get; }

    /// <summary>Whether every symbol starts an atom that may be moved or removed on its own.</summary>
    public bool HasSubsections { get; private set; }

    /// <summary>Input sections; the section number used by symbols and relocations is the index plus one.</summary>
    public MachInputSection[] Sections { get; private set; } = [];

    /// <summary>
    /// The symbol table, followed by the anchors: unnamed local symbols that the linker adds so that relocations
    /// which refer to a section by address can be expressed against a symbol like all others.
    /// </summary>
    public IReadOnlyList<MachNlist> Nlist => _nlist;

    /// <summary>Resolved symbols by index into <see cref="Nlist"/>. Filled in by the linker.</summary>
    public MachSymbol[] Symbols { get; set; } = [];

    /// <summary>The oldest OS version the object was compiled for, encoded as <c>xxxx.yy.zz</c> nibbles; 0 if unknown.</summary>
    public uint MinimumOSVersion { get; private set; }

    public static bool IsMachObject(ReadOnlySpan<byte> data) => data.Length >= 4 && LittleEndian.U32(data, 0) == MachHeader.Magic64;

    /// <summary>Creates an empty object that hosts linker-synthesized sections and symbols.</summary>
    public static MachObjectFile CreateSynthetic(string name, uint cpuType) => new(name, cpuType) { HasSubsections = true };

    /// <summary>Adds a section with a single atom to a synthetic object and returns the atom.</summary>
    public Atom AddSyntheticSection(string segmentName, string sectionName, ulong size, ulong alignment, uint flags, ReadOnlyMemory<byte> content)
    {
        var section = new MachInputSection(this, Sections.Length, segmentName, sectionName, 0, size, alignment, flags, content);
        var atom = new Atom(section, 0, size, section.Alignment);
        section.Atoms = [atom];
        Sections = [.. Sections, section];
        return atom;
    }

    /// <summary>
    /// Makes a symbol referable from the relocations of a synthetic object and returns its index in
    /// <see cref="Symbols"/>.
    /// </summary>
    public int AddSyntheticSymbol(MachSymbol symbol)
    {
        if (!_syntheticSymbols.TryGetValue(symbol, out var index))
        {
            index = Symbols.Length;
            Symbols = [.. Symbols, symbol];
            _syntheticSymbols.Add(symbol, index);
        }

        return index;
    }

    public static MachObjectFile Parse(string name, ReadOnlyMemory<byte> memory)
    {
        var data = memory.Span;
        if (data.Length < MachHeader.Size || !IsMachObject(data))
        {
            throw new LinkerException($"{name}: not a 64-bit Mach-O file");
        }

        if (LittleEndian.U32(data, 12) != MachHeader.FileTypeObject)
        {
            throw new LinkerException($"{name}: not a relocatable object file");
        }

        var file = new MachObjectFile(name, LittleEndian.U32(data, 4))
        {
            HasSubsections = (LittleEndian.U32(data, 24) & MachHeader.SubsectionsViaSymbols) != 0,
        };

        var commandCount = (int)LittleEndian.U32(data, 16);
        var commandsSize = (int)LittleEndian.U32(data, 20);
        if (MachHeader.Size + (long)commandsSize > data.Length)
        {
            throw new LinkerException($"{name}: load commands extend past the end of the file");
        }

        var sections = new List<MachInputSection>();
        var offset = MachHeader.Size;
        for (var i = 0; i < commandCount; i++)
        {
            if (offset + 8 > MachHeader.Size + commandsSize)
            {
                throw new LinkerException($"{name}: malformed load command table");
            }

            var command = LittleEndian.U32(data, offset);
            var size = (int)LittleEndian.U32(data, offset + 4);
            if (size < 8 || offset + size > MachHeader.Size + commandsSize)
            {
                throw new LinkerException($"{name}: malformed load command {i}");
            }

            var body = data.Slice(offset, size);
            switch (command)
            {
                case LoadCommand.Segment64:
                    file.ReadSections(memory, body, sections);
                    break;
                case LoadCommand.SymTab:
                    file.ReadSymbols(data, body);
                    break;
                case LoadCommand.BuildVersion:
                    file.MinimumOSVersion = LittleEndian.U32(body, 12);
                    break;
                case LoadCommand.VersionMinMacOS:
                    file.MinimumOSVersion = LittleEndian.U32(body, 8);
                    break;
            }

            offset += size;
        }

        file.Sections = [.. sections];
        try
        {
            file.CreateAtoms();
            file.DecodeRelocations();
            MachUnwindReader.Read(file);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            // Offsets inside sections and tables are only checked where a wrong value would go unnoticed.
            throw new LinkerException($"{name}: malformed object file", e);
        }

        return file;
    }

    /// <summary>
    /// Returns the index of a symbol that marks the start of <paramref name="atom"/>, creating it when needed.
    /// </summary>
    public int AnchorFor(Atom atom)
    {
        if (!_anchors.TryGetValue(atom, out var index))
        {
            index = _nlist.Count;
            _nlist.Add(new MachNlist(string.Empty, MachSymbolType.Section, (byte)(atom.Section.Index + 1), 0, atom.InputAddress));
            _anchors.Add(atom, index);
        }

        return index;
    }

    /// <summary>Finds the atom at an address inside the object file, preferring the section with the given number.</summary>
    public Atom? FindAtom(ulong address, int sectionNumber = 0)
    {
        if (sectionNumber >= 1 && sectionNumber <= Sections.Length && Sections[sectionNumber - 1].FindAtom(address) is { } preferred)
        {
            return preferred;
        }

        foreach (var section in Sections)
        {
            if (section.Kind == MachSectionKind.Regular && address >= section.Address && address < section.Address + section.Size
                && section.FindAtom(address) is { } atom)
            {
                return atom;
            }
        }

        return null;
    }

    /// <summary>
    /// Evaluates a field of an unwind table as if the object were loaded at the addresses it uses internally.
    /// Assemblers express such fields in several ways (explicit symbol differences, section-relative values or no
    /// relocation at all); evaluating them yields the address the field means, whatever the form.
    /// </summary>
    public MachFieldValue EvaluateField(MachInputSection section, int offset, int size)
    {
        var content = section.Content.Span;
        var value = size == 8 ? LittleEndian.I64(content, offset) : LittleEndian.I32(content, offset);
        var external = -1;
        var isGot = false;
        var hasRelocation = false;
        var target = MachTarget.ForCpu(CpuType, Name);

        foreach (var index in section.RelocationsAt(offset))
        {
            ref readonly var relocation = ref section.RawRelocations[index];
            hasRelocation = true;
            if (target.IsGotPointerRelocation(relocation.Type))
            {
                external = relocation.SymbolNumber;
                isGot = true;
                continue;
            }

            var isSubtractor = target.IsSubtractorRelocation(relocation.Type);
            if (!isSubtractor && !target.IsUnsignedRelocation(relocation.Type))
            {
                throw new LinkerException(
                    $"{section.DisplayName}+0x{offset:x}: unsupported relocation {target.RelocationName(relocation.Type)} in an unwind table");
            }

            if (!relocation.IsExtern)
            {
                // The content already contains the address of the section the relocation refers to.
                continue;
            }

            var symbol = _nlist[relocation.SymbolNumber];
            if (symbol.IsDefinedInSection || symbol.IsAbsolute)
            {
                value += isSubtractor ? -(long)symbol.Value : (long)symbol.Value;
            }
            else if (!isSubtractor)
            {
                external = relocation.SymbolNumber;
            }
            else
            {
                throw new LinkerException($"{section.DisplayName}+0x{offset:x}: difference to the undefined symbol '{symbol.Name}'");
            }
        }

        return new MachFieldValue(value, external, isGot, hasRelocation);
    }

    private static string FixedName(ReadOnlySpan<byte> field)
    {
        var length = field.IndexOf((byte)0);
        return Encoding.ASCII.GetString(length < 0 ? field : field[..length]);
    }

    private void ReadSections(ReadOnlyMemory<byte> memory, ReadOnlySpan<byte> command, List<MachInputSection> sections)
    {
        var data = memory.Span;
        var count = (int)LittleEndian.U32(command, 64);
        if (LoadCommand.SegmentSize + (long)count * LoadCommand.SectionSize > command.Length)
        {
            throw new LinkerException($"{Name}: malformed segment command");
        }

        for (var i = 0; i < count; i++)
        {
            var header = command.Slice(LoadCommand.SegmentSize + i * LoadCommand.SectionSize, LoadCommand.SectionSize);
            var sectionName = FixedName(header[..16]);
            var segmentName = FixedName(header.Slice(16, 16));
            var address = LittleEndian.U64(header, 32);
            var size = LittleEndian.U64(header, 40);
            var fileOffset = LittleEndian.U32(header, 48);
            var alignment = LittleEndian.U32(header, 52);
            var relocationOffset = LittleEndian.U32(header, 56);
            var relocationCount = LittleEndian.U32(header, 60);
            var flags = LittleEndian.U32(header, 64);
            if (alignment > 30)
            {
                throw new LinkerException($"{Name}: section {segmentName},{sectionName} has an invalid alignment");
            }

            var type = flags & MachSectionType.TypeMask;
            var isZeroFill = type is MachSectionType.ZeroFill or MachSectionType.GigabyteZeroFill or MachSectionType.ThreadLocalZeroFill;
            var content = ReadOnlyMemory<byte>.Empty;
            if (!isZeroFill)
            {
                if (fileOffset + size > (ulong)data.Length)
                {
                    throw new LinkerException($"{Name}: section {segmentName},{sectionName} extends past the end of the file");
                }

                content = memory.Slice((int)fileOffset, (int)size);
            }

            var section = new MachInputSection(this, sections.Count, segmentName, sectionName, address, size, 1UL << (int)alignment, flags, content)
            {
                Kind = Classify(segmentName, sectionName),
            };

            if (relocationCount > 0 && section.Kind != MachSectionKind.Ignored)
            {
                if (relocationOffset + (ulong)relocationCount * RelocationSize > (ulong)data.Length)
                {
                    throw new LinkerException($"{Name}: relocations of {segmentName},{sectionName} extend past the end of the file");
                }

                var relocations = new MachRawRelocation[relocationCount];
                for (var r = 0; r < relocations.Length; r++)
                {
                    var entry = data.Slice((int)relocationOffset + r * RelocationSize, RelocationSize);
                    var word = LittleEndian.U32(entry, 4);
                    relocations[r] = new MachRawRelocation(
                        LittleEndian.I32(entry, 0), (int)(word & 0xffffff), (word & (1 << 24)) != 0, (byte)((word >> 25) & 3),
                        (word & (1 << 27)) != 0, (byte)(word >> 28));
                }

                section.RawRelocations = relocations;
            }

            sections.Add(section);
        }
    }

    private static MachSectionKind Classify(string segmentName, string sectionName)
    {
        if (segmentName is "__DWARF" or "__LLVM")
        {
            return MachSectionKind.Ignored;
        }

        if (segmentName == "__LD")
        {
            return sectionName == "__compact_unwind" ? MachSectionKind.CompactUnwind : MachSectionKind.Ignored;
        }

        if (segmentName == "__TEXT" && sectionName == "__eh_frame")
        {
            return MachSectionKind.EhFrame;
        }

        if (sectionName == "__objc_imageinfo")
        {
            return MachSectionKind.ObjCImageInfo;
        }

        // The debug attribute (S_ATTR_DEBUG) is deliberately not consulted: ILCompiler sets it on the table with the
        // exception handling data of managed code, which the runtime reads. Only the segment tells what is debug
        // information.
        return MachSectionKind.Regular;
    }

    private void ReadSymbols(ReadOnlySpan<byte> data, ReadOnlySpan<byte> command)
    {
        var symbolOffset = LittleEndian.U32(command, 8);
        var symbolCount = LittleEndian.U32(command, 12);
        var stringOffset = LittleEndian.U32(command, 16);
        var stringSize = LittleEndian.U32(command, 20);
        if (symbolOffset + (ulong)symbolCount * MachSymbolType.EntrySize > (ulong)data.Length || stringOffset + (ulong)stringSize > (ulong)data.Length)
        {
            throw new LinkerException($"{Name}: symbol table extends past the end of the file");
        }

        var strings = data.Slice((int)stringOffset, (int)stringSize);
        _nlist.Capacity = (int)symbolCount;
        for (var i = 0; i < symbolCount; i++)
        {
            var entry = data.Slice((int)symbolOffset + i * MachSymbolType.EntrySize, MachSymbolType.EntrySize);
            _nlist.Add(new MachNlist(
                LittleEndian.CString(strings, (int)LittleEndian.U32(entry, 0)), entry[4], entry[5], LittleEndian.U16(entry, 6), LittleEndian.U64(entry, 8)));
        }
    }

    /// <summary>Splits the regular sections into atoms at the symbols (and, for string literals, at every string).</summary>
    private void CreateAtoms()
    {
        var boundaries = new List<ulong>[Sections.Length];
        var keep = new List<ulong>?[Sections.Length];
        foreach (var symbol in _nlist)
        {
            if (symbol.IsStab || !symbol.IsDefinedInSection || symbol.SectionNumber == 0 || symbol.SectionNumber > Sections.Length)
            {
                continue;
            }

            var index = symbol.SectionNumber - 1;
            var section = Sections[index];
            if (section.Kind != MachSectionKind.Regular || symbol.Value < section.Address || symbol.Value > section.Address + section.Size)
            {
                continue;
            }

            if (symbol.IsNoDeadStrip)
            {
                (keep[index] ??= []).Add(symbol.Value);
            }

            // Alternate entry points and assembler-local labels are positions inside an atom, not atoms.
            var startsAtom = HasSubsections && !symbol.IsAltEntry && !(symbol.Name.StartsWith('L') && !symbol.IsExternal);
            (boundaries[index] ??= []).Add(startsAtom ? symbol.Value : section.Address);
        }

        for (var i = 0; i < Sections.Length; i++)
        {
            var section = Sections[i];
            if (section.Kind != MachSectionKind.Regular || (section.Size == 0 && boundaries[i] is null))
            {
                continue;
            }

            var starts = boundaries[i] ?? [];
            starts.Add(section.Address);
            if (section.Type == MachSectionType.CStringLiterals)
            {
                // Every string is its own atom, so that unused strings of a used object file can be dropped.
                var content = section.Content.Span;
                for (var position = 0; position < content.Length;)
                {
                    var length = content[position..].IndexOf((byte)0);
                    if (length < 0)
                    {
                        break;
                    }

                    position += length + 1;
                    if (position < content.Length)
                    {
                        starts.Add(section.Address + (ulong)position);
                    }
                }
            }

            starts.Sort();
            var atoms = new List<Atom>(starts.Count);
            for (var s = 0; s < starts.Count; s++)
            {
                if (s > 0 && starts[s] == starts[s - 1])
                {
                    continue;
                }

                var next = s + 1;
                while (next < starts.Count && starts[next] == starts[s])
                {
                    next++;
                }

                var end = next < starts.Count ? starts[next] : section.Address + section.Size;
                var inputOffset = starts[s] - section.Address;

                // An atom keeps the alignment that its position in the section happened to have, which is at least
                // what the compiler needed.
                var alignment = inputOffset == 0 ? section.Alignment : Math.Min(section.Alignment, 1UL << System.Numerics.BitOperations.TrailingZeroCount(inputOffset));
                atoms.Add(new Atom(section, inputOffset, end - starts[s], alignment) { IsNoDeadStrip = section.IsNoDeadStrip });
            }

            section.Atoms = [.. atoms];
            if (keep[i] is { } kept)
            {
                foreach (var address in kept)
                {
                    section.FindAtom(address)!.IsNoDeadStrip = true;
                }
            }
        }
    }

    private void DecodeRelocations()
    {
        MachTarget? target = null;
        foreach (var section in Sections)
        {
            if (section.Kind != MachSectionKind.Regular || section.RawRelocations.Length == 0)
            {
                continue;
            }

            target ??= MachTarget.ForCpu(CpuType, Name);
            var relocations = target.Decode(this, section);
            relocations.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));

            // Hand every atom its relocations, with offsets relative to the atom.
            var position = 0;
            foreach (var atom in section.Atoms)
            {
                var first = position;
                while (position < relocations.Count && relocations[position].Offset < atom.InputOffset + atom.Size)
                {
                    position++;
                }

                if (position == first)
                {
                    continue;
                }

                var own = new MachRelocation[position - first];
                for (var r = 0; r < own.Length; r++)
                {
                    var relocation = relocations[first + r];
                    own[r] = relocation with { Offset = relocation.Offset - (uint)atom.InputOffset };
                }

                atom.Relocations = own;
            }

            if (position < relocations.Count)
            {
                throw new LinkerException($"{section.DisplayName}: relocation at 0x{relocations[position].Offset:x} is outside of the section");
            }
        }
    }
}
