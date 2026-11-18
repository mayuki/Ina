using Ina.Linker.Elf;

namespace Ina.Linker.Tests.Support;

/// <summary>A minimal reader for linked executables, used to inspect what the linker produced.</summary>
internal sealed class ElfImage
{
    private readonly byte[] _data;

    public ElfImage(byte[] data)
    {
        _data = data;
        Type = LittleEndian.U16(data, 0x10);
        Machine = LittleEndian.U16(data, 0x12);
        Entry = LittleEndian.U64(data, 0x18);

        var programHeaderOffset = (int)LittleEndian.U64(data, 0x20);
        var programHeaderCount = LittleEndian.U16(data, 0x38);
        for (var i = 0; i < programHeaderCount; i++)
        {
            var h = data.AsSpan(programHeaderOffset + i * 56, 56);
            Segments.Add(new Segment(
                LittleEndian.U32(h, 0), LittleEndian.U32(h, 4), LittleEndian.U64(h, 8), LittleEndian.U64(h, 16),
                LittleEndian.U64(h, 32), LittleEndian.U64(h, 40), LittleEndian.U64(h, 48)));
        }

        var sectionHeaderOffset = (int)LittleEndian.U64(data, 0x28);
        var sectionCount = LittleEndian.U16(data, 0x3c);
        var nameTableIndex = LittleEndian.U16(data, 0x3e);
        var raw = new List<(uint Name, Section Section)>();
        for (var i = 0; i < sectionCount; i++)
        {
            var h = data.AsSpan(sectionHeaderOffset + i * 64, 64);
            raw.Add((LittleEndian.U32(h, 0), new Section(
                string.Empty, LittleEndian.U32(h, 4), LittleEndian.U64(h, 8), LittleEndian.U64(h, 16), LittleEndian.U64(h, 24),
                LittleEndian.U64(h, 32), LittleEndian.U32(h, 40), LittleEndian.U32(h, 44))));
        }

        var names = raw[nameTableIndex].Section;
        foreach (var (name, section) in raw)
        {
            Sections.Add(section with { Name = LittleEndian.CString(data.AsSpan((int)names.Offset, (int)names.Size), (int)name) });
        }

        if (FindSection(".symtab") is { } symtab)
        {
            foreach (var (name, value) in ReadSymbols(symtab))
            {
                Symbols[name] = value;
            }
        }

        if (FindSection(".dynsym") is { } dynsym)
        {
            DynamicSymbols.AddRange(ReadSymbols(dynsym).Select(static s => s.Name));
        }

        if (FindSection(".dynamic") is { } dynamic)
        {
            var strings = SectionData(Sections[(int)dynamic.Link]);
            var content = SectionData(dynamic);
            for (var offset = 0; offset + 16 <= content.Length; offset += 16)
            {
                var tag = LittleEndian.I64(content, offset);
                var value = LittleEndian.U64(content, offset + 8);
                if (tag == DynamicTag.Null)
                {
                    break;
                }

                DynamicEntries.Add((tag, value));
                if (tag == DynamicTag.Needed)
                {
                    Needed.Add(LittleEndian.CString(strings, (int)value));
                }
            }
        }
    }

    public sealed record Segment(uint Type, uint Flags, ulong Offset, ulong Address, ulong FileSize, ulong MemorySize, ulong Alignment);

    public sealed record Section(string Name, uint Type, ulong Flags, ulong Address, ulong Offset, ulong Size, uint Link, uint Info);

    public ushort Type { get; }

    public ushort Machine { get; }

    public ulong Entry { get; }

    public List<Segment> Segments { get; } = [];

    public List<Section> Sections { get; } = [];

    /// <summary>Symbols of <c>.symtab</c> by name.</summary>
    public Dictionary<string, ulong> Symbols { get; } = new(StringComparer.Ordinal);

    public List<string> DynamicSymbols { get; } = [];

    public List<(long Tag, ulong Value)> DynamicEntries { get; } = [];

    public List<string> Needed { get; } = [];

    public string? Interpreter => FindSection(".interp") is { } interp ? LittleEndian.CString(SectionData(interp), 0) : null;

    public Section? FindSection(string name) => Sections.Find(s => s.Name == name);

    public Section GetSection(string name) => FindSection(name) ?? throw new InvalidOperationException($"no section named {name}");

    public ReadOnlySpan<byte> SectionData(Section section) =>
        section.Type == SectionType.NoBits ? [] : _data.AsSpan((int)section.Offset, (int)section.Size);

    /// <summary>Reads memory of the image at a virtual address through the section table.</summary>
    public ReadOnlySpan<byte> ReadAt(ulong address, int length)
    {
        foreach (var section in Sections)
        {
            if (section.Type != SectionType.NoBits && section.Address != 0 && address >= section.Address && address + (ulong)length <= section.Address + section.Size)
            {
                return _data.AsSpan((int)(section.Offset + (address - section.Address)), length);
            }
        }

        throw new InvalidOperationException($"address 0x{address:x} is not backed by the file");
    }

    private List<(string Name, ulong Value)> ReadSymbols(Section table)
    {
        var strings = Sections[(int)table.Link];
        var result = new List<(string, ulong)>();
        var content = SectionData(table);
        for (var offset = 24; offset + 24 <= content.Length; offset += 24)
        {
            var name = LittleEndian.CString(SectionData(strings), (int)LittleEndian.U32(content, offset));
            result.Add((name, LittleEndian.U64(content, offset + 8)));
        }

        return result;
    }
}
