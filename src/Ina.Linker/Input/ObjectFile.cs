using Ina.Linker.Elf;
using Ina.Linker.Symbols;

namespace Ina.Linker.Input;

/// <summary>A relocatable ELF64 little-endian object file.</summary>
internal sealed class ObjectFile
{
    private const int ElfHeaderSize = 64;
    private const int SectionHeaderSize = 64;
    private const int SymbolSize = 24;
    private const int RelaSize = 24;

    private ObjectFile(string name, ElfMachine machine)
    {
        Name = name;
        Machine = machine;
    }

    /// <summary>Display name, e.g. <c>libfoo.a(bar.o)</c>.</summary>
    public string Name { get; }

    public ElfMachine Machine { get; }

    /// <summary>Input sections by ELF section index. Null for sections that do not take part in the link.</summary>
    public InputSection?[] Sections { get; private set; } = [];

    public ElfSymbol[] ElfSymbols { get; private set; } = [];

    /// <summary>Index of the first non-local symbol in <see cref="ElfSymbols"/>.</summary>
    public int FirstGlobalSymbol { get; private set; }

    /// <summary>Resolved symbols by ELF symbol index. Filled in by the linker.</summary>
    public Symbol[] Symbols { get; set; } = [];

    /// <summary>CIE and FDE records of all <c>.eh_frame</c> sections, in file order.</summary>
    public List<EhFrameRecord> EhFrameRecords { get; } = [];

    /// <summary>Creates an empty object that hosts linker-synthesized sections and symbols.</summary>
    public static ObjectFile CreateSynthetic(string name, ElfMachine machine) => new(name, machine);

    /// <summary>Sets the sections and symbols of a synthetic object.</summary>
    public void SetSyntheticContent(InputSection?[] sections, ElfSymbol[] symbols, int firstGlobalSymbol)
    {
        Sections = sections;
        ElfSymbols = symbols;
        FirstGlobalSymbol = firstGlobalSymbol;
    }

    public static bool IsElf(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[0] == 0x7f && data[1] == (byte)'E' && data[2] == (byte)'L' && data[3] == (byte)'F';

    /// <summary>Parses an object file.</summary>
    /// <param name="claimComdat">
    /// Called with the signature of every COMDAT group; returns false when a group with the same signature was already
    /// taken from an earlier file, in which case the group's sections are discarded.
    /// </param>
    public static ObjectFile Parse(string name, ReadOnlyMemory<byte> memory, Func<string, bool> claimComdat)
    {
        var data = memory.Span;
        if (data.Length < ElfHeaderSize || !IsElf(data))
        {
            throw new LinkerException($"{name}: not an ELF file");
        }

        if (data[4] != 2 || data[5] != 1)
        {
            throw new LinkerException($"{name}: only 64-bit little-endian ELF files are supported");
        }

        if (LittleEndian.U16(data, 0x10) != ElfType.Rel)
        {
            throw new LinkerException($"{name}: not a relocatable object file");
        }

        var file = new ObjectFile(name, (ElfMachine)LittleEndian.U16(data, 0x12));
        var headers = ReadSectionHeaders(name, data);
        var sectionNames = SectionData(name, data, headers[SectionNameTableIndex(data, headers)]);

        // The symbol table comes first: COMDAT signatures and relocations refer to it.
        var extendedIndices = ReadOnlySpan<byte>.Empty;
        foreach (var header in headers)
        {
            if (header.Type == SectionType.SymTabShndx)
            {
                extendedIndices = SectionData(name, data, header);
            }
        }

        foreach (var header in headers)
        {
            if (header.Type == SectionType.SymTab)
            {
                file.ReadSymbols(name, data, headers, header, sectionNames, extendedIndices);
                break;
            }
        }

        // COMDAT groups decide which sections are discarded before any section is materialized.
        var discarded = new bool[headers.Length];
        foreach (var header in headers)
        {
            if (header.Type != SectionType.Group)
            {
                continue;
            }

            var group = SectionData(name, data, header);
            if (group.Length < 4 || (LittleEndian.U32(group, 0) & 1) == 0)
            {
                // Not a COMDAT group: its members are ordinary sections.
                continue;
            }

            if (header.Info >= file.ElfSymbols.Length)
            {
                throw new LinkerException($"{name}: COMDAT group refers to an invalid signature symbol");
            }

            if (claimComdat(file.ElfSymbols[header.Info].Name))
            {
                continue;
            }

            for (var offset = 4; offset + 4 <= group.Length; offset += 4)
            {
                var member = LittleEndian.U32(group, offset);
                if (member < discarded.Length)
                {
                    discarded[member] = true;
                }
            }
        }

        file.Sections = new InputSection?[headers.Length];
        var ehFrameSections = new List<int>();
        for (var i = 1; i < headers.Length; i++)
        {
            var header = headers[i];
            if (discarded[i] || !IsLinkedSectionType(header.Type))
            {
                continue;
            }

            if ((header.Flags & SectionFlags.Alloc) == 0 || (header.Flags & SectionFlags.Exclude) != 0)
            {
                // Debug information, comments and other non-loaded data are dropped.
                continue;
            }

            var sectionName = LittleEndian.CString(sectionNames, (int)header.Name);
            var content = header.Type == SectionType.NoBits
                ? ReadOnlyMemory<byte>.Empty
                : memory.Slice(CheckedOffset(name, data, header), (int)header.Size);
            file.Sections[i] = new InputSection(file, i, sectionName, header.Type, header.Flags, header.Size, header.AddrAlign, content);
            if (sectionName == ".eh_frame")
            {
                ehFrameSections.Add(i);
            }
        }

        foreach (var header in headers)
        {
            if (header.Type == SectionType.Rel && header.Info < headers.Length && file.Sections[header.Info] is not null)
            {
                throw new LinkerException($"{name}: REL relocations are not supported");
            }

            if (header.Type != SectionType.Rela || header.Info >= headers.Length || file.Sections[header.Info] is not { } target)
            {
                continue;
            }

            target.Relocations = ReadRelocations(SectionData(name, data, header));
        }

        // .eh_frame is split into records and never emitted as an ordinary section.
        foreach (var index in ehFrameSections)
        {
            var section = file.Sections[index]!;
            file.Sections[index] = null;
            file.SplitEhFrame(section, memory.Slice(CheckedOffset(name, data, headers[index]), (int)headers[index].Size));
        }

        return file;
    }

    private static bool IsLinkedSectionType(uint type) => type is
        SectionType.ProgBits or SectionType.NoBits or SectionType.InitArray or SectionType.FiniArray or
        SectionType.PreInitArray or SectionType.X86_64Unwind;

    private static int SectionNameTableIndex(ReadOnlySpan<byte> data, SectionHeader[] headers)
    {
        int index = LittleEndian.U16(data, 0x3e);
        if (index == SectionIndex.XIndex)
        {
            index = (int)headers[0].Link;
        }

        return index < headers.Length ? index : 0;
    }

    private static SectionHeader[] ReadSectionHeaders(string name, ReadOnlySpan<byte> data)
    {
        var offset = LittleEndian.U64(data, 0x28);
        int count = LittleEndian.U16(data, 0x3c);
        if (offset == 0)
        {
            return [];
        }

        if (offset + SectionHeaderSize > (ulong)data.Length)
        {
            throw new LinkerException($"{name}: truncated section header table");
        }

        if (count == 0)
        {
            // More than 0xff00 sections: the real count lives in the first section header.
            count = checked((int)LittleEndian.U64(data, (int)offset + 32));
        }

        if (offset + (ulong)count * SectionHeaderSize > (ulong)data.Length)
        {
            throw new LinkerException($"{name}: truncated section header table");
        }

        var headers = new SectionHeader[count];
        for (var i = 0; i < count; i++)
        {
            var h = data.Slice((int)offset + i * SectionHeaderSize, SectionHeaderSize);
            headers[i] = new SectionHeader(
                LittleEndian.U32(h, 0), LittleEndian.U32(h, 4), LittleEndian.U64(h, 8), LittleEndian.U64(h, 24),
                LittleEndian.U64(h, 32), LittleEndian.U32(h, 40), LittleEndian.U32(h, 44), LittleEndian.U64(h, 48));
        }

        return headers;
    }

    private static int CheckedOffset(string name, ReadOnlySpan<byte> data, SectionHeader header)
    {
        if (header.Offset > (ulong)data.Length || header.Size > (ulong)data.Length - header.Offset)
        {
            throw new LinkerException($"{name}: section data extends past the end of the file");
        }

        return (int)header.Offset;
    }

    private static ReadOnlySpan<byte> SectionData(string name, ReadOnlySpan<byte> data, SectionHeader header) =>
        header.Type == SectionType.NoBits ? [] : data.Slice(CheckedOffset(name, data, header), (int)header.Size);

    private static Relocation[] ReadRelocations(ReadOnlySpan<byte> data)
    {
        var relocations = new Relocation[data.Length / RelaSize];
        for (var i = 0; i < relocations.Length; i++)
        {
            var offset = i * RelaSize;
            var info = LittleEndian.U64(data, offset + 8);
            relocations[i] = new Relocation(LittleEndian.U64(data, offset), (uint)info, (uint)(info >> 32), LittleEndian.I64(data, offset + 16));
        }

        return relocations;
    }

    private void ReadSymbols(
        string name, ReadOnlySpan<byte> data, SectionHeader[] headers, SectionHeader symtab, ReadOnlySpan<byte> sectionNames,
        ReadOnlySpan<byte> extendedIndices)
    {
        if (symtab.Link >= headers.Length)
        {
            throw new LinkerException($"{name}: symbol table has no string table");
        }

        var symbols = SectionData(name, data, symtab);
        var strings = SectionData(name, data, headers[symtab.Link]);
        var count = symbols.Length / SymbolSize;
        ElfSymbols = new ElfSymbol[count];
        FirstGlobalSymbol = (int)Math.Min(symtab.Info, (uint)count);

        for (var i = 0; i < count; i++)
        {
            var s = symbols.Slice(i * SymbolSize, SymbolSize);
            var info = s[4];
            var raw = LittleEndian.U16(s, 6);
            var section = -1;
            if (raw == SectionIndex.XIndex && extendedIndices.Length >= (i + 1) * 4)
            {
                section = (int)LittleEndian.U32(extendedIndices, i * 4);
            }
            else if (raw is not SectionIndex.Undefined and < SectionIndex.LoReserve)
            {
                section = raw;
            }

            var symbolName = LittleEndian.CString(strings, (int)LittleEndian.U32(s, 0));
            if ((info & 0xf) == SymbolType.Section && symbolName.Length == 0 && section >= 0 && section < headers.Length)
            {
                // Section symbols are unnamed; borrow the section name for diagnostics and COMDAT signatures.
                symbolName = LittleEndian.CString(sectionNames, (int)headers[section].Name);
            }

            ElfSymbols[i] = new ElfSymbol(symbolName, info, s[5], raw, section, LittleEndian.U64(s, 8), LittleEndian.U64(s, 16));
        }
    }

    /// <summary>Splits an <c>.eh_frame</c> section into its CIE and FDE records and distributes the relocations.</summary>
    private void SplitEhFrame(InputSection section, ReadOnlyMemory<byte> memory)
    {
        var data = memory.Span;
        var relocations = section.Relocations;
        Array.Sort(relocations, static (a, b) => a.Offset.CompareTo(b.Offset));
        var nextRelocation = 0;
        var cies = new Dictionary<int, EhFrameRecord>();

        var offset = 0;
        while (offset + 4 <= data.Length)
        {
            var length = LittleEndian.U32(data, offset);
            if (length == 0)
            {
                // Terminator; the linker emits its own at the very end of the output.
                break;
            }

            if (length == 0xffffffff)
            {
                throw new LinkerException($"{section.DisplayName}: 64-bit DWARF .eh_frame records are not supported");
            }

            var size = (int)length + 4;
            if (size < 8 || offset + size > data.Length)
            {
                throw new LinkerException($"{section.DisplayName}: malformed .eh_frame record at offset 0x{offset:x}");
            }

            var id = LittleEndian.U32(data, offset + 4);
            var record = new EhFrameRecord(this, memory.Slice(offset, size), isCie: id == 0);

            // Rebase the relocations that fall inside this record so that they are relative to the record start.
            var first = nextRelocation;
            while (nextRelocation < relocations.Length && relocations[nextRelocation].Offset < (ulong)(offset + size))
            {
                nextRelocation++;
            }

            var recordRelocations = new Relocation[nextRelocation - first];
            for (var i = 0; i < recordRelocations.Length; i++)
            {
                var r = relocations[first + i];
                recordRelocations[i] = r with { Offset = r.Offset - (ulong)offset };
            }

            record.Relocations = recordRelocations;

            if (record.IsCie)
            {
                cies[offset] = record;
            }
            else
            {
                // The CIE pointer is the distance from the pointer field itself back to the CIE.
                var cieOffset = offset + 4 - (int)id;
                if (!cies.TryGetValue(cieOffset, out var cie))
                {
                    throw new LinkerException($"{section.DisplayName}: FDE at offset 0x{offset:x} refers to an unknown CIE");
                }

                record.Cie = cie;
                foreach (var r in recordRelocations)
                {
                    // The first field after the CIE pointer is the start address of the described code.
                    if (r.Offset != 8)
                    {
                        continue;
                    }

                    record.PcBeginRelocation = r;
                    var symbol = ElfSymbols[r.SymbolIndex];
                    if (symbol.Section >= 0 && symbol.Section < Sections.Length)
                    {
                        record.Target = Sections[symbol.Section];
                    }

                    break;
                }
            }

            EhFrameRecords.Add(record);
            offset += size;
        }
    }

    private readonly record struct SectionHeader(uint Name, uint Type, ulong Flags, ulong Offset, ulong Size, uint Link, uint Info, ulong AddrAlign);
}
