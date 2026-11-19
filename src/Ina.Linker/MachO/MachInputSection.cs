namespace Ina.Linker.MachO;

/// <summary>A relocation entry as stored in an object file (<c>relocation_info</c>).</summary>
internal readonly record struct MachRawRelocation(int Address, int SymbolNumber, bool PcRel, byte Length, bool IsExtern, byte Type);

/// <summary>A section of an input object file.</summary>
internal sealed class MachInputSection
{
    public MachInputSection(
        MachObjectFile file, int index, string segmentName, string sectionName, ulong address, ulong size, ulong alignment, uint flags,
        ReadOnlyMemory<byte> content)
    {
        File = file;
        Index = index;
        SegmentName = segmentName;
        SectionName = sectionName;
        Address = address;
        Size = size;
        Alignment = Math.Max(alignment, 1);
        Flags = flags;
        Content = content;
    }

    public MachObjectFile File { get; }

    /// <summary>The zero-based position in the object file; symbols and relocations use the number <c>Index + 1</c>.</summary>
    public int Index { get; }

    public string SegmentName { get; }

    public string SectionName { get; }

    /// <summary>
    /// The address of the section inside the object file. Object files are laid out as if they were loaded at
    /// address zero, and symbols and section-relative relocations use these addresses.
    /// </summary>
    public ulong Address { get; }

    public ulong Size { get; }

    public ulong Alignment { get; }

    public uint Flags { get; }

    public ReadOnlyMemory<byte> Content { get; }

    public MachSectionKind Kind { get; set; }

    /// <summary>The atoms of a regular section, ordered by address.</summary>
    public Atom[] Atoms { get; set; } = [];

    public MachRawRelocation[] RawRelocations { get; set; } = [];

    /// <summary>Positions in <see cref="RawRelocations"/> ordered by the address they apply to; built on first use.</summary>
    private int[]? _relocationsByAddress;

    public uint Type => Flags & MachSectionType.TypeMask;

    public bool IsZeroFill => Type is MachSectionType.ZeroFill or MachSectionType.GigabyteZeroFill or MachSectionType.ThreadLocalZeroFill;

    public bool IsCode => (Flags & MachSectionType.AttrPureInstructions) != 0 || (SegmentName == "__TEXT" && SectionName == "__text");

    /// <summary>
    /// Whether the content is kept although nothing refers to it. Compilers mark the references to Objective-C
    /// classes like that too, but honoring it there would make the image import every class that an unused
    /// function mentions. Apple's linker keeps only the class references that live code uses.
    /// </summary>
    public bool IsNoDeadStrip => (Flags & MachSectionType.AttrNoDeadStrip) != 0 && !IsClassReferences;

    private bool IsClassReferences => SegmentName == "__DATA" && SectionName == "__objc_classrefs";

    public string DisplayName => $"{File.Name}:({SegmentName},{SectionName})";

    /// <summary>
    /// Returns the positions in <see cref="RawRelocations"/> of the entries that apply to the field at
    /// <paramref name="offset"/>, in file order (which matters for entries that form a pair).
    /// </summary>
    public ReadOnlySpan<int> RelocationsAt(int offset)
    {
        if (_relocationsByAddress is null)
        {
            // Unwind tables have a few relocations per record and are read field by field; without an index every
            // field would search the whole table.
            var order = new int[RawRelocations.Length];
            for (var i = 0; i < order.Length; i++)
            {
                order[i] = i;
            }

            var relocations = RawRelocations;
            Array.Sort(order, (a, b) => relocations[a].Address != relocations[b].Address ? relocations[a].Address.CompareTo(relocations[b].Address) : a.CompareTo(b));
            _relocationsByAddress = order;
        }

        var low = 0;
        var high = _relocationsByAddress.Length;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (RawRelocations[_relocationsByAddress[middle]].Address < offset)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        var end = low;
        while (end < _relocationsByAddress.Length && RawRelocations[_relocationsByAddress[end]].Address == offset)
        {
            end++;
        }

        return _relocationsByAddress.AsSpan(low, end - low);
    }

    /// <summary>
    /// Finds the atom that contains <paramref name="address"/> (an address inside the object file). An address at
    /// the very end of the section belongs to the last atom.
    /// </summary>
    public Atom? FindAtom(ulong address)
    {
        if (address < Address || address > Address + Size || Atoms.Length == 0)
        {
            return null;
        }

        var offset = address - Address;
        var low = 0;
        var high = Atoms.Length - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (Atoms[middle].InputOffset <= offset)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return Atoms[low];
    }

    public override string ToString() => DisplayName;
}

/// <summary>
/// The unit of linking: a run of bytes of an input section that is kept or discarded as a whole and that may move
/// independently of its neighbours. Object files that use "subsections via symbols" promise that every symbol
/// starts such a unit; other sections form a single atom.
/// </summary>
internal sealed class Atom
{
    public Atom(MachInputSection section, ulong inputOffset, ulong size, ulong alignment)
    {
        Section = section;
        InputOffset = inputOffset;
        Size = size;
        Alignment = alignment;
    }

    public MachInputSection Section { get; }

    public MachObjectFile File => Section.File;

    /// <summary>Offset of the atom inside its input section.</summary>
    public ulong InputOffset { get; }

    public ulong Size { get; }

    public ulong Alignment { get; }

    /// <summary>The address the atom has inside the object file.</summary>
    public ulong InputAddress => Section.Address + InputOffset;

    /// <summary>Relocations with offsets relative to the start of the atom, in ascending order.</summary>
    public MachRelocation[] Relocations { get; set; } = [];

    /// <summary>Unwind records that describe code of this atom. They are kept exactly when the atom is kept.</summary>
    public List<UnwindRecord>? UnwindRecords { get; set; }

    /// <summary>Whether the object file asks for the atom to be kept even when nothing refers to it.</summary>
    public bool IsNoDeadStrip { get; set; }

    /// <summary>Whether the atom survived dead stripping and is part of the output.</summary>
    public bool IsLive { get; set; }

    /// <summary>The output section this atom was assigned to. Null until layout.</summary>
    public MachOutputSection? Parent { get; set; }

    /// <summary>Offset of this atom inside <see cref="Parent"/>.</summary>
    public ulong Offset { get; set; }

    /// <summary>The address of the atom in the output image. Valid after layout.</summary>
    public ulong Address => Parent is null ? 0 : Parent.Address + Offset;

    public ReadOnlySpan<byte> Content => Section.IsZeroFill ? default : Section.Content.Span.Slice((int)InputOffset, (int)Size);

    public string DisplayName => Section.DisplayName;

    public override string ToString() => $"{DisplayName}+0x{InputOffset:x}";
}
