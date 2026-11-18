using System.Text;
using Ina.Linker.Elf;

namespace Ina.Linker.Tests.Support;

/// <summary>
/// Builds relocatable ELF objects in memory, so that the linker can be tested without a native assembler or compiler.
/// </summary>
internal sealed class ElfObjectBuilder(ElfMachine machine = ElfMachine.X86_64)
{
    private readonly List<Section> _sections = [];
    private readonly List<SymbolEntry> _symbols = [];
    private readonly List<(string Signature, Section[] Members)> _groups = [];

    public sealed class Section
    {
        public required string Name { get; init; }

        public required uint Type { get; init; }

        public required ulong Flags { get; set; }

        public required byte[] Data { get; init; }

        public required ulong Size { get; init; }

        public required ulong Alignment { get; init; }

        internal List<(ulong Offset, uint Type, object Symbol, long Addend)> Relocations { get; } = [];

        internal int Index { get; set; }
    }

    private sealed record SymbolEntry(string Name, Section? Section, ushort SpecialIndex, ulong Value, ulong Size, byte Type, byte Binding);

    public Section AddSection(string name, ReadOnlySpan<byte> data, ulong flags, uint type = SectionType.ProgBits, ulong alignment = 1)
    {
        var section = new Section { Name = name, Type = type, Flags = flags, Data = data.ToArray(), Size = (ulong)data.Length, Alignment = alignment };
        _sections.Add(section);
        return section;
    }

    public Section Text(string name, params byte[] code) => AddSection(name, code, SectionFlags.Alloc | SectionFlags.ExecInstr, alignment: 16);

    public Section ReadOnlyData(string name, ReadOnlySpan<byte> data, ulong alignment = 1) => AddSection(name, data, SectionFlags.Alloc, alignment: alignment);

    public Section Data(string name, ReadOnlySpan<byte> data, ulong alignment = 8) =>
        AddSection(name, data, SectionFlags.Alloc | SectionFlags.Write, alignment: alignment);

    public Section NoBits(string name, ulong size, ulong flags = SectionFlags.Alloc | SectionFlags.Write, ulong alignment = 8)
    {
        var section = new Section { Name = name, Type = SectionType.NoBits, Flags = flags, Data = [], Size = size, Alignment = alignment };
        _sections.Add(section);
        return section;
    }

    public void Global(string name, Section section, ulong value = 0, ulong size = 0, byte type = SymbolType.Func) =>
        _symbols.Add(new SymbolEntry(name, section, 0, value, size, type, SymbolBinding.Global));

    public void Weak(string name, Section section, ulong value = 0, ulong size = 0, byte type = SymbolType.Func) =>
        _symbols.Add(new SymbolEntry(name, section, 0, value, size, type, SymbolBinding.Weak));

    public void Local(string name, Section section, ulong value = 0, ulong size = 0, byte type = SymbolType.Func) =>
        _symbols.Add(new SymbolEntry(name, section, 0, value, size, type, SymbolBinding.Local));

    public void Common(string name, ulong size, ulong alignment) =>
        _symbols.Add(new SymbolEntry(name, null, SectionIndex.Common, alignment, size, SymbolType.Object, SymbolBinding.Global));

    /// <summary>Declares an undefined weak symbol. Strong undefined symbols are created implicitly by relocations.</summary>
    public void WeakUndefined(string name) =>
        _symbols.Add(new SymbolEntry(name, null, SectionIndex.Undefined, 0, 0, SymbolType.NoType, SymbolBinding.Weak));

    /// <summary>Adds a relocation against a named symbol.</summary>
    public void Relocate(Section section, ulong offset, uint type, string symbol, long addend = 0) =>
        section.Relocations.Add((offset, type, symbol, addend));

    /// <summary>Adds a relocation against the start of a section, the way compilers refer to local data.</summary>
    public void Relocate(Section section, ulong offset, uint type, Section target, long addend = 0) =>
        section.Relocations.Add((offset, type, target, addend));

    /// <summary>Puts sections into a COMDAT group; the linker keeps only one group per signature.</summary>
    public void Comdat(string signature, params Section[] members)
    {
        _groups.Add((signature, members));
        foreach (var member in members)
        {
            member.Flags |= SectionFlags.Group;
        }
    }

    public byte[] Build()
    {
        // Section table: null, user sections, relocation sections, groups, .symtab, .strtab, .shstrtab.
        for (var i = 0; i < _sections.Count; i++)
        {
            _sections[i].Index = i + 1;
        }

        var relocated = _sections.Where(static s => s.Relocations.Count > 0).ToList();
        var firstRela = _sections.Count + 1;
        var firstGroup = firstRela + relocated.Count;
        var symtabIndex = firstGroup + _groups.Count;
        var strtabIndex = symtabIndex + 1;
        var shstrtabIndex = symtabIndex + 2;
        var sectionCount = shstrtabIndex + 1;

        // Symbols: null, one section symbol per user section, locals, then globals (including implicit undefined ones).
        var ordered = new List<SymbolEntry>();
        foreach (var section in _sections)
        {
            ordered.Add(new SymbolEntry(string.Empty, section, 0, 0, 0, SymbolType.Section, SymbolBinding.Local));
        }

        ordered.AddRange(_symbols.Where(static s => s.Binding == SymbolBinding.Local));
        var firstGlobal = ordered.Count + 1;
        ordered.AddRange(_symbols.Where(static s => s.Binding != SymbolBinding.Local));
        foreach (var name in _sections.SelectMany(static s => s.Relocations).Select(static r => r.Symbol).OfType<string>()
                     .Concat(_groups.Select(static g => g.Signature)).Distinct())
        {
            if (!ordered.Exists(s => s.Name == name))
            {
                ordered.Add(new SymbolEntry(name, null, SectionIndex.Undefined, 0, 0, SymbolType.NoType, SymbolBinding.Global));
            }
        }

        int SymbolIndex(object symbol) => symbol is Section target
            ? target.Index
            : ordered.FindIndex(s => s.Name == (string)symbol && s.Type != SymbolType.Section) + 1;

        var strings = new StringTable();
        var symtab = new byte[(ordered.Count + 1) * 24];
        for (var i = 0; i < ordered.Count; i++)
        {
            var symbol = ordered[i];
            var entry = symtab.AsSpan((i + 1) * 24, 24);
            LittleEndian.U32(entry, 0, strings.Add(symbol.Name));
            entry[4] = (byte)((symbol.Binding << 4) | symbol.Type);
            LittleEndian.U16(entry, 6, symbol.Section is { } section ? (ushort)section.Index : symbol.SpecialIndex);
            LittleEndian.U64(entry, 8, symbol.Value);
            LittleEndian.U64(entry, 16, symbol.Size);
        }

        var sectionNames = new StringTable();
        var headers = new List<(uint Name, uint Type, ulong Flags, byte[] Data, ulong Size, uint Link, uint Info, ulong Alignment, ulong EntrySize)>();
        foreach (var section in _sections)
        {
            headers.Add((sectionNames.Add(section.Name), section.Type, section.Flags, section.Data, section.Size, 0, 0, section.Alignment, 0));
        }

        foreach (var section in relocated)
        {
            var data = new byte[section.Relocations.Count * 24];
            for (var i = 0; i < section.Relocations.Count; i++)
            {
                var (offset, type, symbol, addend) = section.Relocations[i];
                LittleEndian.U64(data, i * 24, offset);
                LittleEndian.U64(data, i * 24 + 8, ((ulong)SymbolIndex(symbol) << 32) | type);
                LittleEndian.I64(data, i * 24 + 16, addend);
            }

            headers.Add((sectionNames.Add(".rela" + section.Name), SectionType.Rela, SectionFlags.InfoLink, data, (ulong)data.Length, (uint)symtabIndex, (uint)section.Index, 8, 24));
        }

        foreach (var (signature, members) in _groups)
        {
            var data = new byte[4 + members.Length * 4];
            LittleEndian.U32(data, 0, 1); // GRP_COMDAT
            for (var i = 0; i < members.Length; i++)
            {
                LittleEndian.U32(data, 4 + i * 4, (uint)members[i].Index);
            }

            headers.Add((sectionNames.Add(".group"), SectionType.Group, 0, data, (ulong)data.Length, (uint)symtabIndex, (uint)SymbolIndex(signature), 4, 4));
        }

        headers.Add((sectionNames.Add(".symtab"), SectionType.SymTab, 0, symtab, (ulong)symtab.Length, (uint)strtabIndex, (uint)firstGlobal, 8, 24));
        var strtab = strings.ToArray();
        headers.Add((sectionNames.Add(".strtab"), SectionType.StrTab, 0, strtab, (ulong)strtab.Length, 0, 0, 1, 0));
        var shstrtabName = sectionNames.Add(".shstrtab");
        var shstrtab = sectionNames.ToArray();
        headers.Add((shstrtabName, SectionType.StrTab, 0, shstrtab, (ulong)shstrtab.Length, 0, 0, 1, 0));

        using var stream = new MemoryStream();
        stream.Write(new byte[64]);
        var offsets = new long[headers.Count];
        for (var i = 0; i < headers.Count; i++)
        {
            while (stream.Length % 8 != 0)
            {
                stream.WriteByte(0);
            }

            offsets[i] = stream.Length;
            stream.Write(headers[i].Data);
        }

        while (stream.Length % 8 != 0)
        {
            stream.WriteByte(0);
        }

        var sectionHeaderOffset = stream.Length;
        stream.Write(new byte[sectionCount * 64]);
        var image = stream.ToArray();

        image[0] = 0x7f;
        image[1] = (byte)'E';
        image[2] = (byte)'L';
        image[3] = (byte)'F';
        image[4] = 2;
        image[5] = 1;
        image[6] = 1;
        LittleEndian.U16(image, 0x10, ElfType.Rel);
        LittleEndian.U16(image, 0x12, (ushort)machine);
        LittleEndian.U32(image, 0x14, 1);
        LittleEndian.U64(image, 0x28, (ulong)sectionHeaderOffset);
        LittleEndian.U16(image, 0x34, 64);
        LittleEndian.U16(image, 0x3a, 64);
        LittleEndian.U16(image, 0x3c, (ushort)sectionCount);
        LittleEndian.U16(image, 0x3e, (ushort)shstrtabIndex);

        for (var i = 0; i < headers.Count; i++)
        {
            var h = headers[i];
            var header = image.AsSpan((int)sectionHeaderOffset + (i + 1) * 64, 64);
            LittleEndian.U32(header, 0, h.Name);
            LittleEndian.U32(header, 4, h.Type);
            LittleEndian.U64(header, 8, h.Flags);
            LittleEndian.U64(header, 24, (ulong)offsets[i]);
            LittleEndian.U64(header, 32, h.Size);
            LittleEndian.U32(header, 40, h.Link);
            LittleEndian.U32(header, 44, h.Info);
            LittleEndian.U64(header, 48, h.Alignment);
            LittleEndian.U64(header, 56, h.EntrySize);
        }

        return image;
    }

    private sealed class StringTable
    {
        private readonly MemoryStream _data = new();
        private readonly Dictionary<string, uint> _offsets = new() { [string.Empty] = 0 };

        public StringTable()
        {
            _data.WriteByte(0);
        }

        public uint Add(string value)
        {
            if (!_offsets.TryGetValue(value, out var offset))
            {
                offset = (uint)_data.Length;
                _data.Write(Encoding.UTF8.GetBytes(value));
                _data.WriteByte(0);
                _offsets[value] = offset;
            }

            return offset;
        }

        public byte[] ToArray() => _data.ToArray();
    }
}
