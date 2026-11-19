using System.Globalization;
using System.Text;
using Ina.Linker.Elf;

namespace Ina.Linker.Pe;

/// <summary>A relocation of a COFF section. The addend is stored in the relocated field itself.</summary>
internal readonly record struct CoffRelocation(uint Offset, int SymbolIndex, ushort Type);

/// <summary>A symbol table entry of a COFF object together with what its auxiliary records say.</summary>
/// <param name="SectionNumber">
/// One-based index of the defining section; 0 for undefined and common symbols, -1 for absolute symbols and -2 for
/// debugging symbols.
/// </param>
internal readonly record struct CoffSymbolRecord(string Name, uint Value, int SectionNumber, byte StorageClass)
{
    /// <summary>For a weak external, the index of the symbol that is used when no definition is found.</summary>
    public int WeakDefaultIndex { get; init; }

    /// <summary>Whether the entry is an auxiliary record of the preceding symbol rather than a symbol.</summary>
    public bool IsAuxiliary { get; init; }

    public bool IsExternal => StorageClass == CoffStorageClass.External;

    public bool IsWeakExternal => StorageClass == CoffStorageClass.WeakExternal;

    public bool IsUndefined => SectionNumber == 0 && Value == 0;

    /// <summary>An uninitialized global of C code: no section, and the value is its size.</summary>
    public bool IsCommon => IsExternal && SectionNumber == 0 && Value != 0;

    public bool IsAbsolute => SectionNumber == -1;
}

/// <summary>A section of a COFF object that takes part in the link.</summary>
internal sealed class CoffSection : PeChunk
{
    private readonly ReadOnlyMemory<byte> _data;

    public CoffSection(CoffObject file, int index, string name, uint characteristics, uint alignment, uint size, ReadOnlyMemory<byte> data)
    {
        File = file;
        Index = index;
        Name = name;
        Characteristics = characteristics & CoffSectionFlags.ImageMask;
        HasComdatFlag = (characteristics & CoffSectionFlags.LinkComdat) != 0;
        Alignment = Math.Max(alignment, 1);
        Size = size;
        _data = data;
    }

    /// <summary>The object file this section came from; relocation symbol indices are relative to it.</summary>
    public CoffObject File { get; }

    /// <summary>The one-based section number inside the object file.</summary>
    public int Index { get; }

    public string Name { get; }

    public CoffRelocation[] Relocations { get; set; } = [];

    /// <summary>Whether the section header marks the section as a COMDAT section.</summary>
    public bool HasComdatFlag { get; }

    /// <summary>How duplicates of the section are handled (<see cref="CoffComdat"/>); 0 for ordinary sections.</summary>
    public byte ComdatSelection { get; set; }

    /// <summary>Index of the symbol that identifies a COMDAT section, or -1.</summary>
    public int ComdatLeader { get; set; } = -1;

    /// <summary>For an associative COMDAT section, the number of the section it belongs to.</summary>
    public int AssociatedSection { get; set; }

    /// <summary>The associative sections that follow this section: its unwind data, for example.</summary>
    public List<CoffSection>? Children { get; private set; }

    /// <summary>Whether another copy of this COMDAT section was chosen, so that this one is not linked.</summary>
    public bool IsDiscarded { get; private set; }

    public bool IsComdat => ComdatSelection != CoffComdat.None;

    public override string SectionName => Name;

    public override uint Characteristics { get; }

    public override uint Alignment { get; }

    public override uint Size { get; }

    public override string DisplayName => $"{File.Name}:({Name})";

    public void AddChild(CoffSection child) => (Children ??= []).Add(child);

    /// <summary>Removes the section and everything that only exists because of it from the link.</summary>
    public void Discard()
    {
        if (IsDiscarded)
        {
            return;
        }

        IsDiscarded = true;
        if (Children is not null)
        {
            foreach (var child in Children)
            {
                child.Discard();
            }
        }
    }

    public override void Write(Span<byte> destination, PeLinkContext context)
    {
        _data.Span.CopyTo(destination);
        context.ApplyRelocations(this, destination);
    }
}

/// <summary>A relocatable COFF object file, in the regular or the "bigobj" flavor.</summary>
internal sealed class CoffObject
{
    private const int HeaderSize = 20;
    private const int BigObjHeaderSize = 56;
    private const int SectionHeaderSize = 40;
    private const int RelocationSize = 10;
    private const int MaxRegularSectionNumber = 0xfeff;

    /// <summary>The class id that marks an anonymous object header as a bigobj header.</summary>
    private static ReadOnlySpan<byte> BigObjClassId =>
        [0xc7, 0xa1, 0xba, 0xd1, 0xee, 0xba, 0xa9, 0x4b, 0xaf, 0x20, 0xfa, 0xf6, 0x6a, 0xa4, 0xdc, 0xb8];

    /// <summary>By section number: the section symbol, the COMDAT selection and the associated section.</summary>
    private readonly Dictionary<int, (int SymbolIndex, byte Selection, int Associated)> _sectionDefinitions = [];

    private CoffObject(string name, ushort machine)
    {
        Name = name;
        Machine = machine;
    }

    /// <summary>Display name, e.g. <c>Runtime.lib(gc.obj)</c>.</summary>
    public string Name { get; }

    public ushort Machine { get; }

    /// <summary>Sections by one-based section number. Null for sections that do not take part in the link.</summary>
    public CoffSection?[] Sections { get; private set; } = [];

    /// <summary>The symbol table, including placeholders for auxiliary records so that indices stay valid.</summary>
    public CoffSymbolRecord[] Records { get; private set; } = [];

    /// <summary>Resolved symbols by symbol table index. Filled in by the linker.</summary>
    public PeSymbol[] Symbols { get; set; } = [];

    /// <summary>The linker options embedded in the object (<c>.drectve</c>), one option per entry.</summary>
    public List<string> Directives { get; } = [];

    /// <summary>Whether the data looks like a COFF object for an architecture Ina knows.</summary>
    public static bool IsObject(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
        {
            return false;
        }

        return LittleEndian.U16(data, 0) is CoffMachine.Amd64 or CoffMachine.Arm64 || IsBigObj(data);
    }

    /// <summary>
    /// Whether the data is a short import object, the member format of import libraries. Ina binds imports through
    /// its import table instead and does not read these.
    /// </summary>
    public static bool IsImportObject(ReadOnlySpan<byte> data) =>
        data.Length >= HeaderSize && LittleEndian.U16(data, 0) == CoffMachine.Unknown && LittleEndian.U16(data, 2) == 0xffff && !IsBigObj(data);

    private static bool IsBigObj(ReadOnlySpan<byte> data) =>
        data.Length >= BigObjHeaderSize && LittleEndian.U16(data, 0) == CoffMachine.Unknown && LittleEndian.U16(data, 2) == 0xffff
        && LittleEndian.U16(data, 4) >= 2 && data.Slice(12, 16).SequenceEqual(BigObjClassId);

    public static CoffObject Parse(string name, ReadOnlyMemory<byte> memory)
    {
        var data = memory.Span;
        if (!IsObject(data))
        {
            throw new LinkerException($"{name}: not a COFF object for x64 or ARM64");
        }

        var isBigObj = IsBigObj(data);
        var machine = LittleEndian.U16(data, isBigObj ? 6 : 0);
        var sectionCount = isBigObj ? checked((int)LittleEndian.U32(data, 44)) : LittleEndian.U16(data, 2);
        var symbolTableOffset = checked((int)LittleEndian.U32(data, isBigObj ? 48 : 8));
        var symbolCount = checked((int)LittleEndian.U32(data, isBigObj ? 52 : 12));
        var sectionTableOffset = isBigObj ? BigObjHeaderSize : HeaderSize + LittleEndian.U16(data, 16);
        var symbolSize = isBigObj ? 20 : 18;

        if (sectionTableOffset + (long)sectionCount * SectionHeaderSize > data.Length || symbolTableOffset + (long)symbolCount * symbolSize > data.Length)
        {
            throw new LinkerException($"{name}: truncated COFF object");
        }

        var file = new CoffObject(name, machine);
        var strings = symbolCount == 0 ? [] : data[(symbolTableOffset + symbolCount * symbolSize)..];
        file.ReadSymbols(data.Slice(symbolTableOffset, symbolCount * symbolSize), strings, symbolSize, sectionCount);
        file.ReadSections(memory, sectionTableOffset, sectionCount, strings);
        file.ReadComdatInformation();
        return file;
    }

    private void ReadSymbols(ReadOnlySpan<byte> table, ReadOnlySpan<byte> strings, int symbolSize, int sectionCount)
    {
        var count = table.Length / symbolSize;
        var isBigObj = symbolSize == 20;
        Records = new CoffSymbolRecord[count];
        for (var i = 0; i < count; i++)
        {
            var entry = table.Slice(i * symbolSize, symbolSize);
            var symbolName = LittleEndian.U32(entry, 0) == 0 ? LittleEndian.CString(strings, (int)LittleEndian.U32(entry, 4)) : ShortName(entry[..8]);
            var value = LittleEndian.U32(entry, 8);
            var sectionNumber = isBigObj ? LittleEndian.I32(entry, 12) : RegularSectionNumber(LittleEndian.U16(entry, 12));
            var storageClass = entry[symbolSize - 2];
            int auxiliaryCount = entry[symbolSize - 1];
            if (sectionNumber > sectionCount || i + auxiliaryCount > count - 1)
            {
                throw new LinkerException($"{Name}: malformed symbol table entry {i}");
            }

            var record = new CoffSymbolRecord(symbolName, value, sectionNumber, storageClass);
            if (storageClass == CoffStorageClass.WeakExternal && auxiliaryCount > 0)
            {
                var auxiliary = table.Slice((i + 1) * symbolSize, symbolSize);
                record = record with { WeakDefaultIndex = (int)LittleEndian.U32(auxiliary, 0) };
            }

            Records[i] = record;
            for (var a = 1; a <= auxiliaryCount; a++)
            {
                Records[i + a] = new CoffSymbolRecord(string.Empty, 0, 0, 0) { IsAuxiliary = true };
            }

            // The section definition is needed again for COMDAT sections; remember where its auxiliary record is.
            if (storageClass == CoffStorageClass.Static && auxiliaryCount > 0 && sectionNumber > 0)
            {
                var auxiliary = table.Slice((i + 1) * symbolSize, symbolSize);
                var associated = (int)LittleEndian.U16(auxiliary, 12);
                if (isBigObj)
                {
                    associated |= LittleEndian.U16(auxiliary, 16) << 16;
                }

                _sectionDefinitions[sectionNumber] = (i, auxiliary[14], associated);
            }

            i += auxiliaryCount;
        }
    }

    private void ReadSections(ReadOnlyMemory<byte> memory, int tableOffset, int count, ReadOnlySpan<byte> strings)
    {
        var data = memory.Span;
        Sections = new CoffSection?[count + 1];
        for (var number = 1; number <= count; number++)
        {
            var header = data.Slice(tableOffset + (number - 1) * SectionHeaderSize, SectionHeaderSize);
            var sectionName = SectionName(header[..8], strings);
            var size = LittleEndian.U32(header, 16);
            var dataOffset = LittleEndian.U32(header, 20);
            var relocationOffset = LittleEndian.U32(header, 24);
            int relocationCount = LittleEndian.U16(header, 32);
            var characteristics = LittleEndian.U32(header, 36);

            if (sectionName == ".drectve")
            {
                ReadDirectives(Slice(data, dataOffset, size, sectionName));
                continue;
            }

            if (!IsLinked(sectionName, characteristics))
            {
                continue;
            }

            var isUninitialized = (characteristics & CoffSectionFlags.UninitializedData) != 0 || dataOffset == 0;
            var content = isUninitialized ? ReadOnlyMemory<byte>.Empty : memory.Slice(checked((int)dataOffset), Slice(data, dataOffset, size, sectionName).Length);
            var section = new CoffSection(this, number, sectionName, characteristics, AlignmentOf(characteristics), size, content);

            if (relocationCount > 0)
            {
                var first = 0;
                if ((characteristics & CoffSectionFlags.RelocationOverflow) != 0 && relocationCount == 0xffff)
                {
                    // The real count does not fit; it is stored in the first relocation, which is not a relocation.
                    relocationCount = checked((int)LittleEndian.U32(Slice(data, relocationOffset, RelocationSize, sectionName), 0));
                    first = 1;
                }

                var table = Slice(data, relocationOffset, (uint)(relocationCount * RelocationSize), sectionName);
                var relocations = new CoffRelocation[relocationCount - first];
                for (var i = first; i < relocationCount; i++)
                {
                    var entry = table.Slice(i * RelocationSize, RelocationSize);
                    var symbolIndex = (int)LittleEndian.U32(entry, 4);
                    if ((uint)symbolIndex >= (uint)Records.Length)
                    {
                        throw new LinkerException($"{Name}:({sectionName}): relocation refers to an invalid symbol");
                    }

                    relocations[i - first] = new CoffRelocation(LittleEndian.U32(entry, 0), symbolIndex, LittleEndian.U16(entry, 8));
                }

                section.Relocations = relocations;
            }

            Sections[number] = section;
        }
    }

    /// <summary>
    /// Whether a section becomes part of the image. Debug information is dropped because Ina does not write a
    /// program database, and the tables for Control Flow Guard are dropped because the image does not enable it.
    /// </summary>
    private static bool IsLinked(string name, uint characteristics)
    {
        if ((characteristics & (CoffSectionFlags.LinkRemove | CoffSectionFlags.LinkInfo | CoffSectionFlags.Discardable)) != 0)
        {
            return false;
        }

        return !name.StartsWith(".debug", StringComparison.Ordinal)
            && name is not (".gfids$y" or ".giats$y" or ".gljmp$y" or ".gehcont$y" or ".voltbl" or ".llvm_addrsig" or ".chks64");
    }

    private static uint AlignmentOf(uint characteristics)
    {
        var shift = (int)((characteristics & CoffSectionFlags.AlignMask) >> 20);

        // No alignment bits means the default of 16 bytes.
        return shift == 0 ? 16u : 1u << (shift - 1);
    }

    private ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> data, uint offset, uint size, string sectionName)
    {
        if (offset > (uint)data.Length || size > (uint)data.Length - offset)
        {
            throw new LinkerException($"{Name}:({sectionName}): section data extends past the end of the file");
        }

        return data.Slice((int)offset, (int)size);
    }

    /// <summary>
    /// Finds out which sections are COMDAT sections, what identifies them and which sections are associated with
    /// another one. The section symbol carries the selection; the next symbol of the same section is the COMDAT
    /// symbol.
    /// </summary>
    private void ReadComdatInformation()
    {
        var awaitingLeader = new HashSet<int>();
        for (var i = 0; i < Records.Length; i++)
        {
            var record = Records[i];
            if (record.IsAuxiliary || record.SectionNumber <= 0 || Sections[record.SectionNumber] is not { } section)
            {
                continue;
            }

            if (_sectionDefinitions.TryGetValue(record.SectionNumber, out var definition) && definition.SymbolIndex == i)
            {
                // The selection of a section symbol only means something when the section header has the COMDAT flag.
                if (section.HasComdatFlag && definition.Selection != CoffComdat.None)
                {
                    section.ComdatSelection = definition.Selection;
                    section.AssociatedSection = definition.Associated;
                    if (definition.Selection != CoffComdat.Associative)
                    {
                        awaitingLeader.Add(record.SectionNumber);
                    }
                }

                continue;
            }

            if (awaitingLeader.Remove(record.SectionNumber))
            {
                section.ComdatLeader = i;
            }
        }
    }

    private string SectionName(ReadOnlySpan<byte> field, ReadOnlySpan<byte> strings)
    {
        var text = ShortName(field);

        // Names longer than eight characters live in the string table and are referenced as "/<decimal offset>".
        return text.Length > 1 && text[0] == '/' && int.TryParse(text.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
            ? LittleEndian.CString(strings, offset)
            : text;
    }

    /// <summary>
    /// The section number of a symbol of a regular object. The field is declared as a signed 16-bit number, but
    /// objects count up to 65279 sections (0xfeff) before compilers switch to the bigobj format, so only the values
    /// above that are the special negative numbers, like -1 (0xffff) for absolute symbols.
    /// </summary>
    private static int RegularSectionNumber(ushort field) => field <= MaxRegularSectionNumber ? field : (short)field;

    private static string ShortName(ReadOnlySpan<byte> field)
    {
        var length = field.IndexOf((byte)0);
        return Encoding.UTF8.GetString(length < 0 ? field : field[..length]);
    }

    /// <summary>Splits the text of a <c>.drectve</c> section into options; quotes group and are removed.</summary>
    private void ReadDirectives(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith("﻿"u8))
        {
            data = data[3..];
        }

        var text = Encoding.UTF8.GetString(data);
        var option = new StringBuilder();
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && (char.IsWhiteSpace(c) || c == '\0'))
            {
                Flush();
            }
            else
            {
                option.Append(c);
            }
        }

        Flush();

        void Flush()
        {
            if (option.Length > 0)
            {
                Directives.Add(option.ToString());
                option.Clear();
            }
        }
    }
}
