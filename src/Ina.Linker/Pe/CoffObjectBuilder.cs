using System.Numerics;
using System.Text;
using Ina.Linker.Elf;

namespace Ina.Linker.Pe;

/// <summary>
/// Writes relocatable COFF objects. The linker uses it for the code and data it contributes itself, which then
/// take part in the link like any other object; the tests use it to describe inputs without an assembler.
/// </summary>
internal sealed class CoffObjectBuilder(ushort machine = CoffMachine.Amd64)
{
    private readonly List<Section> _sections = [];
    private readonly List<SymbolEntry> _symbols = [];
    private readonly List<string> _directives = [];

    public sealed class Section
    {
        public required string Name { get; init; }

        public required uint Characteristics { get; set; }

        public required byte[] Data { get; init; }

        public required uint Size { get; init; }

        internal List<(uint Offset, ushort Type, object Symbol)> Relocations { get; } = [];

        internal byte Selection { get; set; }

        internal Section? AssociatedWith { get; set; }

        /// <summary>The one-based section number in the object.</summary>
        internal int Number { get; set; }
    }

    private sealed record SymbolEntry(string Name, Section? Section, int SpecialSection, uint Value, byte StorageClass, string? WeakDefault = null, uint WeakSearch = 0);

    public Section AddSection(string name, ReadOnlySpan<byte> data, uint characteristics, uint alignment = 1)
    {
        var section = new Section { Name = name, Characteristics = characteristics | AlignmentBits(alignment), Data = data.ToArray(), Size = (uint)data.Length };
        _sections.Add(section);
        return section;
    }

    public Section Text(string name, params byte[] code) =>
        AddSection(name, code, CoffSectionFlags.Code | CoffSectionFlags.Execute | CoffSectionFlags.Read, 16);

    public Section ReadOnlyData(string name, ReadOnlySpan<byte> data, uint alignment = 1) =>
        AddSection(name, data, CoffSectionFlags.InitializedData | CoffSectionFlags.Read, alignment);

    public Section Data(string name, ReadOnlySpan<byte> data, uint alignment = 8) =>
        AddSection(name, data, CoffSectionFlags.InitializedData | CoffSectionFlags.Read | CoffSectionFlags.Write, alignment);

    /// <summary>Adds a zero-initialized section, which has a size but no content in the object.</summary>
    public Section Bss(string name, uint size, uint alignment = 8)
    {
        var section = new Section
        {
            Name = name,
            Characteristics = CoffSectionFlags.UninitializedData | CoffSectionFlags.Read | CoffSectionFlags.Write | AlignmentBits(alignment),
            Data = [],
            Size = size,
        };
        _sections.Add(section);
        return section;
    }

    /// <summary>Defines a symbol that other objects can refer to.</summary>
    public void External(string name, Section section, uint value = 0) =>
        _symbols.Add(new SymbolEntry(name, section, 0, value, CoffStorageClass.External));

    /// <summary>Defines a symbol that is only visible inside this object.</summary>
    public void Static(string name, Section section, uint value = 0) =>
        _symbols.Add(new SymbolEntry(name, section, 0, value, CoffStorageClass.Static));

    /// <summary>Declares an uninitialized global, the way a C compiler emits a tentative definition.</summary>
    public void Common(string name, uint size) => _symbols.Add(new SymbolEntry(name, null, 0, size, CoffStorageClass.External));

    public void Absolute(string name, uint value) => _symbols.Add(new SymbolEntry(name, null, -1, value, CoffStorageClass.External));

    /// <summary>Declares a symbol that resolves to <paramref name="defaultSymbol"/> unless something defines it.</summary>
    public void WeakExternal(string name, string defaultSymbol, uint search = CoffWeakSearch.Alias) =>
        _symbols.Add(new SymbolEntry(name, null, 0, 0, CoffStorageClass.WeakExternal, defaultSymbol, search));

    /// <summary>Adds a relocation against a named symbol. The addend is whatever the section data holds at the offset.</summary>
    public void Relocate(Section section, uint offset, ushort type, string symbol) => section.Relocations.Add((offset, type, symbol));

    /// <summary>Adds a relocation against the start of a section, the way compilers refer to local data.</summary>
    public void Relocate(Section section, uint offset, ushort type, Section target) => section.Relocations.Add((offset, type, target));

    /// <summary>
    /// Makes a section a COMDAT section. The first symbol defined in it identifies it, and the linker keeps one
    /// section per identifying symbol.
    /// </summary>
    public void Comdat(Section section, byte selection = CoffComdat.Any)
    {
        section.Characteristics |= CoffSectionFlags.LinkComdat;
        section.Selection = selection;
    }

    /// <summary>Ties a section to another one: it is kept or dropped together with <paramref name="parent"/>.</summary>
    public void Associate(Section section, Section parent)
    {
        Comdat(section, CoffComdat.Associative);
        section.AssociatedWith = parent;
    }

    /// <summary>Embeds a linker option, e.g. <c>/merge:.unbox=.text</c>.</summary>
    public void Directive(string option) => _directives.Add(option);

    public byte[] Build(bool bigObj = false)
    {
        var sections = new List<Section>(_sections);
        if (_directives.Count > 0)
        {
            sections.Add(new Section
            {
                Name = ".drectve",
                Characteristics = CoffSectionFlags.LinkInfo | CoffSectionFlags.LinkRemove | AlignmentBits(1),
                Data = Encoding.UTF8.GetBytes(string.Join(' ', _directives.Select(static d => d.Contains(' ') ? $"\"{d}\"" : d))),
                Size = 0,
            });
        }

        for (var i = 0; i < sections.Count; i++)
        {
            sections[i].Number = i + 1;
        }

        // Symbols: a section symbol with its auxiliary record per section, the declared symbols, and an undefined
        // symbol for every name that is only referenced.
        var symbolSize = bigObj ? 20 : 18;
        var entries = new List<SymbolEntry>(_symbols);
        var referenced = sections.SelectMany(static s => s.Relocations).Select(static r => r.Symbol).OfType<string>()
            .Concat(_symbols.Where(static s => s.WeakDefault is not null).Select(static s => s.WeakDefault!));
        foreach (var name in referenced.Distinct(StringComparer.Ordinal))
        {
            if (!entries.Exists(e => e.Name == name))
            {
                entries.Add(new SymbolEntry(name, null, 0, 0, CoffStorageClass.External));
            }
        }

        // Every entry occupies one slot, plus one for the auxiliary record of weak externals.
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var next = sections.Count * 2;
        foreach (var entry in entries)
        {
            indices.TryAdd(entry.Name, next);
            next += entry.WeakDefault is null ? 1 : 2;
        }

        var symbolCount = next;
        int SymbolIndex(object symbol) => symbol is Section target ? (target.Number - 1) * 2 : indices[(string)symbol];

        var strings = new MemoryStream();
        strings.Write(new byte[4]);
        var symbolTable = new byte[symbolCount * symbolSize];

        void WriteSymbol(int index, string name, uint value, int sectionNumber, byte storageClass, byte auxiliaryCount)
        {
            var entry = symbolTable.AsSpan(index * symbolSize, symbolSize);
            WriteName(entry[..8], name, strings, isSection: false);
            LittleEndian.U32(entry, 8, value);
            if (bigObj)
            {
                LittleEndian.I32(entry, 12, sectionNumber);
            }
            else
            {
                LittleEndian.U16(entry, 12, (ushort)(short)sectionNumber);
            }

            entry[symbolSize - 2] = storageClass;
            entry[symbolSize - 1] = auxiliaryCount;
        }

        foreach (var section in sections)
        {
            var index = (section.Number - 1) * 2;
            WriteSymbol(index, section.Name, 0, section.Number, CoffStorageClass.Static, 1);
            var auxiliary = symbolTable.AsSpan((index + 1) * symbolSize, symbolSize);
            LittleEndian.U32(auxiliary, 0, section.Size);
            LittleEndian.U16(auxiliary, 4, (ushort)Math.Min(section.Relocations.Count, 0xffff));
            var associated = section.AssociatedWith?.Number ?? 0;
            LittleEndian.U16(auxiliary, 12, (ushort)associated);
            auxiliary[14] = section.Selection;
            if (bigObj)
            {
                LittleEndian.U16(auxiliary, 16, (ushort)(associated >> 16));
            }
        }

        foreach (var entry in entries)
        {
            var index = indices[entry.Name];
            if (entry.WeakDefault is null)
            {
                WriteSymbol(index, entry.Name, entry.Value, entry.Section?.Number ?? entry.SpecialSection, entry.StorageClass, 0);
            }
            else
            {
                WriteSymbol(index, entry.Name, 0, 0, CoffStorageClass.WeakExternal, 1);
                var auxiliary = symbolTable.AsSpan((index + 1) * symbolSize, symbolSize);
                LittleEndian.U32(auxiliary, 0, (uint)indices[entry.WeakDefault]);
                LittleEndian.U32(auxiliary, 4, entry.WeakSearch);
            }
        }

        // Layout: header, section table, then the data and the relocations of each section, the symbols, the strings.
        var headerSize = bigObj ? 56 : 20;
        var image = new MemoryStream();
        image.Write(new byte[headerSize + sections.Count * 40]);
        var placements = new (uint Data, uint Relocations)[sections.Count];
        for (var i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            var dataOffset = 0u;
            if (section.Data.Length > 0)
            {
                dataOffset = (uint)image.Length;
                image.Write(section.Data);
            }

            var relocationOffset = 0u;
            if (section.Relocations.Count > 0)
            {
                relocationOffset = (uint)image.Length;
                var relocation = new byte[10];
                foreach (var (offset, type, symbol) in section.Relocations)
                {
                    LittleEndian.U32(relocation, 0, offset);
                    LittleEndian.U32(relocation, 4, (uint)SymbolIndex(symbol));
                    LittleEndian.U16(relocation, 8, type);
                    image.Write(relocation);
                }
            }

            placements[i] = (dataOffset, relocationOffset);
        }

        var symbolTableOffset = (uint)image.Length;
        image.Write(symbolTable);
        var sectionNames = new List<byte[]>();
        foreach (var section in sections)
        {
            var field = new byte[8];
            WriteName(field, section.Name, strings, isSection: true);
            sectionNames.Add(field);
        }

        var stringTable = strings.ToArray();
        LittleEndian.U32(stringTable, 0, (uint)stringTable.Length);
        image.Write(stringTable);

        var result = image.ToArray();
        if (bigObj)
        {
            LittleEndian.U16(result, 2, 0xffff);
            LittleEndian.U16(result, 4, 2);
            LittleEndian.U16(result, 6, machine);
            ReadOnlySpan<byte> classId = [0xc7, 0xa1, 0xba, 0xd1, 0xee, 0xba, 0xa9, 0x4b, 0xaf, 0x20, 0xfa, 0xf6, 0x6a, 0xa4, 0xdc, 0xb8];
            classId.CopyTo(result.AsSpan(12));
            LittleEndian.U32(result, 44, (uint)sections.Count);
            LittleEndian.U32(result, 48, symbolTableOffset);
            LittleEndian.U32(result, 52, (uint)symbolCount);
        }
        else
        {
            LittleEndian.U16(result, 0, machine);
            LittleEndian.U16(result, 2, (ushort)sections.Count);
            LittleEndian.U32(result, 8, symbolTableOffset);
            LittleEndian.U32(result, 12, (uint)symbolCount);
        }

        for (var i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            var header = result.AsSpan(headerSize + i * 40, 40);
            sectionNames[i].CopyTo(header);
            LittleEndian.U32(header, 16, section.Name == ".drectve" ? (uint)section.Data.Length : section.Size);
            LittleEndian.U32(header, 20, placements[i].Data);
            LittleEndian.U32(header, 24, placements[i].Relocations);
            LittleEndian.U16(header, 32, (ushort)section.Relocations.Count);
            LittleEndian.U32(header, 36, section.Characteristics);
        }

        return result;
    }

    private static uint AlignmentBits(uint alignment)
    {
        if (!BitOperations.IsPow2(alignment) || alignment > 8192)
        {
            throw new ArgumentOutOfRangeException(nameof(alignment), alignment, "The alignment of a COFF section is a power of two up to 8192.");
        }

        return (uint)(BitOperations.Log2(alignment) + 1) << 20;
    }

    /// <summary>Names of up to eight bytes are stored inline; longer ones go to the string table.</summary>
    private static void WriteName(Span<byte> field, string name, MemoryStream strings, bool isSection)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        if (bytes.Length <= 8)
        {
            bytes.CopyTo(field);
            return;
        }

        var offset = (uint)strings.Length;
        strings.Write(bytes);
        strings.WriteByte(0);
        if (isSection)
        {
            Encoding.ASCII.GetBytes($"/{offset}").CopyTo(field);
        }
        else
        {
            LittleEndian.U32(field, 4, offset);
        }
    }
}
