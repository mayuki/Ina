using System.Text;
using Ina.Linker.Elf;
using Ina.Linker.MachO;

namespace Ina.Linker.Tests.Support;

/// <summary>
/// Builds relocatable Mach-O objects in memory, so that the linker can be tested without Apple's assembler or
/// compiler. Symbol names are written as they appear in object files, i.e. with the leading underscore of C names.
/// </summary>
internal sealed class MachObjectBuilder(TargetArchitecture architecture = TargetArchitecture.Arm64)
{
    private const uint CodeFlags = MachSectionType.AttrPureInstructions | MachSectionType.AttrSomeInstructions;

    private readonly List<Section> _sections = [];
    private readonly List<SymbolEntry> _symbols = [];
    private readonly List<(Section Function, ulong Offset, uint Length, uint Encoding, string? Personality, Section? Lsda, long LsdaOffset)> _unwindEntries = [];

    public sealed class Section
    {
        public required string Segment { get; init; }

        public required string Name { get; init; }

        public required byte[] Data { get; init; }

        public required ulong Size { get; init; }

        public required int AlignmentLog2 { get; init; }

        public required uint Flags { get; init; }

        internal List<RelocationEntry> Relocations { get; } = [];

        /// <summary>Fields that hold the address of a place in another section, filled in when the object is built.</summary>
        internal List<(uint Offset, int Size, Section Target, long Addend)> AddressFields { get; } = [];

        /// <summary>Fields that hold minus the address of a place, as the subtrahend of a difference contributes it.</summary>
        internal List<(uint Offset, int Size, Section Target, long Addend)> NegatedAddressFields { get; } = [];

        /// <summary>Fields that hold the distance from themselves to a place in another section.</summary>
        internal List<(uint Offset, int Size, Section Target, long Addend)> DistanceFields { get; } = [];

        /// <summary>The address of the section inside the object file. Valid after <see cref="Build"/>.</summary>
        public ulong Address { get; internal set; }

        internal int Number { get; set; }

        internal bool IsZeroFill => (Flags & MachSectionType.TypeMask) is MachSectionType.ZeroFill or MachSectionType.ThreadLocalZeroFill;
    }

    /// <param name="Symbol">A symbol name, a <see cref="Section"/> for section-relative entries, or an addend for ARM64_RELOC_ADDEND.</param>
    internal sealed record RelocationEntry(uint Offset, byte Type, object Symbol, bool PcRel, byte Length);

    private sealed record SymbolEntry(string Name, Section? Section, ulong Value, byte Type, ushort Desc);

    /// <summary>Whether the object declares that every symbol starts an independently movable atom.</summary>
    public bool SubsectionsViaSymbols { get; set; } = true;

    public Section AddSection(string segment, string name, ReadOnlySpan<byte> data, uint flags = MachSectionType.Regular, int alignmentLog2 = 0)
    {
        var section = new Section { Segment = segment, Name = name, Data = data.ToArray(), Size = (ulong)data.Length, AlignmentLog2 = alignmentLog2, Flags = flags };
        _sections.Add(section);
        return section;
    }

    public Section Text(params byte[] code) => Text("__text", code);

    public Section Text(string name, params byte[] code) => AddSection("__TEXT", name, code, CodeFlags, 2);

    public Section CString(ReadOnlySpan<byte> data) => AddSection("__TEXT", "__cstring", data, MachSectionType.CStringLiterals);

    public Section Const(ReadOnlySpan<byte> data, int alignmentLog2 = 3) => AddSection("__TEXT", "__const", data, alignmentLog2: alignmentLog2);

    public Section Data(ReadOnlySpan<byte> data, int alignmentLog2 = 3) => AddSection("__DATA", "__data", data, alignmentLog2: alignmentLog2);

    public Section ZeroFill(string name, ulong size, uint type = MachSectionType.ZeroFill, int alignmentLog2 = 3)
    {
        var section = new Section { Segment = "__DATA", Name = name, Data = [], Size = size, AlignmentLog2 = alignmentLog2, Flags = type };
        _sections.Add(section);
        return section;
    }

    public void Global(string name, Section section, ulong value = 0, ushort desc = 0) =>
        _symbols.Add(new SymbolEntry(name, section, value, MachSymbolType.Section | MachSymbolType.External, desc));

    /// <summary>A symbol that other object files can refer to but that is not exported (hidden visibility).</summary>
    public void PrivateExternal(string name, Section section, ulong value = 0, ushort desc = 0) =>
        _symbols.Add(new SymbolEntry(name, section, value, MachSymbolType.Section | MachSymbolType.External | MachSymbolType.PrivateExternal, desc));

    public void Local(string name, Section section, ulong value = 0, ushort desc = 0) =>
        _symbols.Add(new SymbolEntry(name, section, value, MachSymbolType.Section, desc));

    public void WeakDefinition(string name, Section section, ulong value = 0) => Global(name, section, value, MachSymbolType.WeakDefinition);

    /// <summary>A tentative definition: the size is the value, the alignment is part of the description.</summary>
    public void Common(string name, ulong size, int alignmentLog2) =>
        _symbols.Add(new SymbolEntry(name, null, size, MachSymbolType.Undefined | MachSymbolType.External, (ushort)(alignmentLog2 << 8)));

    /// <summary>Declares an undefined symbol that may be missing at run time. Other undefined symbols are created implicitly.</summary>
    public void WeakReference(string name) =>
        _symbols.Add(new SymbolEntry(name, null, 0, MachSymbolType.Undefined | MachSymbolType.External, MachSymbolType.WeakReference));

    /// <summary>Adds a relocation entry against a named symbol.</summary>
    public void Relocate(Section section, uint offset, byte type, string symbol, bool pcRel, byte length = 2) =>
        section.Relocations.Add(new RelocationEntry(offset, type, symbol, pcRel, length));

    /// <summary>Adds a section-relative relocation entry; the content of the field has to hold the target address.</summary>
    public void Relocate(Section section, uint offset, byte type, Section target, bool pcRel, byte length = 2) =>
        section.Relocations.Add(new RelocationEntry(offset, type, target, pcRel, length));

    /// <summary>Stores the absolute address of a symbol (plus what the field already contains) in a pointer.</summary>
    public void Pointer(Section section, uint offset, string symbol) =>
        Relocate(section, offset, MachRelocArm64.Unsigned, symbol, pcRel: false, length: 3);

    /// <summary>
    /// Stores the address of a place in another section in a pointer, the way compilers refer to data without a
    /// symbol: the field holds the address inside the object file and the relocation names the section.
    /// </summary>
    public void Pointer(Section section, uint offset, Section target, long addend = 0)
    {
        section.AddressFields.Add((offset, 8, target, addend));
        Relocate(section, offset, MachRelocArm64.Unsigned, target, pcRel: false, length: 3);
    }

    /// <summary>
    /// Stores the distance from a field to a place in another section without any relocation, the way x86-64
    /// assemblers fill in unwind tables: the linker has to understand the table to find the reference.
    /// </summary>
    public void Distance(Section section, uint offset, int size, Section target, long targetOffset = 0) =>
        section.DistanceFields.Add((offset, size, target, targetOffset));

    /// <summary>
    /// x86-64: a RIP-relative reference to a place in another section that has no symbol. The displacement holds
    /// the distance inside the object file, and the relocation entry names the section.
    /// </summary>
    /// <param name="trailing">The number of instruction bytes that follow the displacement.</param>
    public void X64SectionRelative(Section section, uint offset, Section target, long targetOffset = 0, int trailing = 0)
    {
        // The distance counts from the end of the instruction, four bytes (plus the trailing ones) behind the field.
        section.DistanceFields.Add((offset, 4, target, targetOffset - 4 - trailing));
        var type = trailing switch
        {
            1 => MachRelocX64.Signed1,
            2 => MachRelocX64.Signed2,
            4 => MachRelocX64.Signed4,
            _ => MachRelocX64.Signed,
        };
        Relocate(section, offset, type, target, pcRel: true);
    }

    /// <summary>Stores <c>minuend - subtrahend</c> (plus what the field already contains) in a field.</summary>
    public void Difference(Section section, uint offset, string minuend, string subtrahend, byte length = 2)
    {
        var (subtractor, unsigned) = architecture == TargetArchitecture.Arm64
            ? (MachRelocArm64.Subtractor, MachRelocArm64.Unsigned)
            : (MachRelocX64.Subtractor, MachRelocX64.Unsigned);

        // Relocation entries are stored in the order given here: the subtractor precedes the entry it modifies.
        Relocate(section, offset, subtractor, subtrahend, pcRel: false, length);
        Relocate(section, offset, unsigned, minuend, pcRel: false, length);
    }

    /// <summary>AArch64: a relocation of an instruction, optionally with an addend (which becomes a separate entry).</summary>
    public void Arm64(Section section, uint offset, byte type, string symbol, int addend = 0)
    {
        if (addend != 0)
        {
            section.Relocations.Add(new RelocationEntry(offset, MachRelocArm64.Addend, addend, false, 2));
        }

        var pcRel = type is MachRelocArm64.Branch26 or MachRelocArm64.Page21 or MachRelocArm64.GotLoadPage21 or MachRelocArm64.TlvpLoadPage21;
        Relocate(section, offset, type, symbol, pcRel);
    }

    /// <summary>
    /// Adds an entry to <c>__LD,__compact_unwind</c>, in which compilers describe how to unwind out of a function.
    /// </summary>
    public void UnwindEntry(Section function, ulong offset, uint length, uint encoding, string? personality = null, Section? lsda = null, long lsdaOffset = 0) =>
        _unwindEntries.Add((function, offset, length, encoding, personality, lsda, lsdaOffset));

    /// <summary>x86-64: a RIP-relative relocation of the 32-bit displacement at <paramref name="offset"/>.</summary>
    public void X64(Section section, uint offset, byte type, string symbol) => Relocate(section, offset, type, symbol, pcRel: true);

    public byte[] Build()
    {
        if (_unwindEntries.Count > 0)
        {
            // An entry is: function address, length, encoding, personality address, address of the language specific data.
            var data = new byte[_unwindEntries.Count * CompactUnwind.EntrySize];
            var section = AddSection("__LD", "__compact_unwind", data, MachSectionType.AttrDebug, 3);
            section.Data.AsSpan().Clear();
            for (var i = 0; i < _unwindEntries.Count; i++)
            {
                var (function, offset, length, encoding, personality, lsda, lsdaOffset) = _unwindEntries[i];
                var entry = (uint)(i * CompactUnwind.EntrySize);
                Pointer(section, entry, function, (long)offset);
                LittleEndian.U32(section.Data, (int)entry + 8, length);
                LittleEndian.U32(section.Data, (int)entry + 12, encoding);
                if (personality is not null)
                {
                    Pointer(section, entry + 16, personality);
                }

                if (lsda is not null)
                {
                    Pointer(section, entry + 24, lsda, lsdaOffset);
                }
            }

            _unwindEntries.Clear();
        }

        var cpuType = architecture == TargetArchitecture.Arm64 ? MachCpu.Arm64 : MachCpu.X64;
        var cpuSubtype = architecture == TargetArchitecture.Arm64 ? MachCpu.SubtypeArm64All : MachCpu.SubtypeX64All;

        // Addresses inside the object: sections follow each other, zero-filled ones last so that the content of the
        // others is contiguous in the file.
        var ordered = _sections.Where(static s => !s.IsZeroFill).Concat(_sections.Where(static s => s.IsZeroFill)).ToList();
        var address = 0UL;
        for (var i = 0; i < ordered.Count; i++)
        {
            var alignment = 1UL << ordered[i].AlignmentLog2;
            address = (address + alignment - 1) & ~(alignment - 1);
            ordered[i].Address = address;
            ordered[i].Number = i + 1;
            address += ordered[i].Size;
        }

        foreach (var section in ordered)
        {
            foreach (var (offset, size, target, addend) in section.DistanceFields)
            {
                var value = (long)target.Address + addend - (long)(section.Address + offset);
                if (size == 8)
                {
                    LittleEndian.I64(section.Data, (int)offset, value);
                }
                else
                {
                    LittleEndian.I32(section.Data, (int)offset, (int)value);
                }
            }

            foreach (var (offset, size, target, addend) in section.NegatedAddressFields)
            {
                LittleEndian.I32(section.Data, (int)offset, (int)-((long)target.Address + addend));
                _ = size;
            }

            foreach (var (offset, size, target, addend) in section.AddressFields)
            {
                var value = (long)target.Address + addend;
                if (size == 8)
                {
                    LittleEndian.I64(section.Data, (int)offset, value);
                }
                else
                {
                    LittleEndian.I32(section.Data, (int)offset, (int)value);
                }
            }
        }

        // Symbols: locals, defined externals, undefined externals (including the ones relocations refer to).
        var symbols = new List<SymbolEntry>();
        symbols.AddRange(_symbols.Where(static s => (s.Type & MachSymbolType.External) == 0));
        var firstExternal = symbols.Count;
        symbols.AddRange(_symbols.Where(static s => (s.Type & MachSymbolType.External) != 0 && s.Section is not null));
        var firstUndefined = symbols.Count;
        symbols.AddRange(_symbols.Where(static s => (s.Type & MachSymbolType.External) != 0 && s.Section is null));
        foreach (var name in _sections.SelectMany(static s => s.Relocations).Select(static r => r.Symbol).OfType<string>().Distinct())
        {
            if (!symbols.Exists(s => s.Name == name))
            {
                symbols.Add(new SymbolEntry(name, null, 0, MachSymbolType.Undefined | MachSymbolType.External, 0));
            }
        }

        var commandsSize = LoadCommand.SegmentSize + ordered.Count * LoadCommand.SectionSize + 24 + 24 + 80;
        var contentOffset = MachHeader.Size + commandsSize;
        var position = contentOffset;
        var fileOffsets = new int[ordered.Count];
        for (var i = 0; i < ordered.Count; i++)
        {
            if (!ordered[i].IsZeroFill)
            {
                fileOffsets[i] = contentOffset + (int)ordered[i].Address;
                position = fileOffsets[i] + ordered[i].Data.Length;
            }
        }

        position = (position + 7) & ~7;
        var relocationOffsets = new int[ordered.Count];
        for (var i = 0; i < ordered.Count; i++)
        {
            relocationOffsets[i] = position;
            position += ordered[i].Relocations.Count * 8;
        }

        var symbolOffset = position;
        position += symbols.Count * MachSymbolType.EntrySize;
        var stringOffset = position;
        var strings = new MemoryStream();
        strings.WriteByte(0);
        var nameOffsets = new uint[symbols.Count];
        for (var i = 0; i < symbols.Count; i++)
        {
            nameOffsets[i] = (uint)strings.Length;
            strings.Write(Encoding.UTF8.GetBytes(symbols[i].Name));
            strings.WriteByte(0);
        }

        var image = new byte[stringOffset + strings.Length];
        LittleEndian.U32(image, 0, MachHeader.Magic64);
        LittleEndian.U32(image, 4, cpuType);
        LittleEndian.U32(image, 8, cpuSubtype);
        LittleEndian.U32(image, 12, MachHeader.FileTypeObject);
        LittleEndian.U32(image, 16, 4);
        LittleEndian.U32(image, 20, (uint)commandsSize);
        LittleEndian.U32(image, 24, SubsectionsViaSymbols ? MachHeader.SubsectionsViaSymbols : 0);

        // One unnamed segment holds all sections, as in every object file.
        var command = image.AsSpan(MachHeader.Size);
        LittleEndian.U32(command, 0, LoadCommand.Segment64);
        LittleEndian.U32(command, 4, (uint)(LoadCommand.SegmentSize + ordered.Count * LoadCommand.SectionSize));
        LittleEndian.U64(command, 32, address);
        LittleEndian.U64(command, 40, (ulong)contentOffset);
        LittleEndian.U64(command, 48, (ulong)(relocationOffsets.Length > 0 ? relocationOffsets[0] - contentOffset : 0));
        LittleEndian.U32(command, 56, 7);
        LittleEndian.U32(command, 60, 7);
        LittleEndian.U32(command, 64, (uint)ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var section = ordered[i];
            var header = command.Slice(LoadCommand.SegmentSize + i * LoadCommand.SectionSize, LoadCommand.SectionSize);
            Encoding.ASCII.GetBytes(section.Name, header);
            Encoding.ASCII.GetBytes(section.Segment, header[16..]);
            LittleEndian.U64(header, 32, section.Address);
            LittleEndian.U64(header, 40, section.Size);
            LittleEndian.U32(header, 48, (uint)fileOffsets[i]);
            LittleEndian.U32(header, 52, (uint)section.AlignmentLog2);
            LittleEndian.U32(header, 56, section.Relocations.Count > 0 ? (uint)relocationOffsets[i] : 0);
            LittleEndian.U32(header, 60, (uint)section.Relocations.Count);
            LittleEndian.U32(header, 64, section.Flags);

            section.Data.CopyTo(image, fileOffsets[i]);
            for (var r = 0; r < section.Relocations.Count; r++)
            {
                var relocation = section.Relocations[r];
                var (number, isExtern) = relocation.Symbol switch
                {
                    string name => (symbols.FindIndex(s => s.Name == name), true),
                    Section target => (target.Number, false),
                    int addend => (addend & 0xffffff, false),
                    _ => throw new InvalidOperationException(),
                };
                var word = (uint)number | (relocation.PcRel ? 1u << 24 : 0) | ((uint)relocation.Length << 25) | (isExtern ? 1u << 27 : 0)
                    | ((uint)relocation.Type << 28);
                LittleEndian.U32(image, relocationOffsets[i] + r * 8, relocation.Offset);
                LittleEndian.U32(image, relocationOffsets[i] + r * 8 + 4, word);
            }
        }

        var next = command[(LoadCommand.SegmentSize + ordered.Count * LoadCommand.SectionSize)..];
        LittleEndian.U32(next, 0, LoadCommand.BuildVersion);
        LittleEndian.U32(next, 4, 24);
        LittleEndian.U32(next, 8, 1);           // macOS
        LittleEndian.U32(next, 12, 12 << 16);   // minimum version 12.0
        LittleEndian.U32(next, 16, 15 << 16);

        next = next[24..];
        LittleEndian.U32(next, 0, LoadCommand.SymTab);
        LittleEndian.U32(next, 4, 24);
        LittleEndian.U32(next, 8, (uint)symbolOffset);
        LittleEndian.U32(next, 12, (uint)symbols.Count);
        LittleEndian.U32(next, 16, (uint)stringOffset);
        LittleEndian.U32(next, 20, (uint)strings.Length);

        next = next[24..];
        LittleEndian.U32(next, 0, LoadCommand.DySymTab);
        LittleEndian.U32(next, 4, 80);
        LittleEndian.U32(next, 12, (uint)firstExternal);
        LittleEndian.U32(next, 16, (uint)firstExternal);
        LittleEndian.U32(next, 20, (uint)(firstUndefined - firstExternal));
        LittleEndian.U32(next, 24, (uint)firstUndefined);
        LittleEndian.U32(next, 28, (uint)(symbols.Count - firstUndefined));

        for (var i = 0; i < symbols.Count; i++)
        {
            var symbol = symbols[i];
            var entry = image.AsSpan(symbolOffset + i * MachSymbolType.EntrySize, MachSymbolType.EntrySize);
            LittleEndian.U32(entry, 0, nameOffsets[i]);
            entry[4] = symbol.Type;
            entry[5] = (byte)(symbol.Section?.Number ?? 0);
            LittleEndian.U16(entry, 6, symbol.Desc);
            LittleEndian.U64(entry, 8, symbol.Section is { } section ? section.Address + symbol.Value : symbol.Value);
        }

        strings.ToArray().CopyTo(image, stringOffset);
        return image;
    }
}
