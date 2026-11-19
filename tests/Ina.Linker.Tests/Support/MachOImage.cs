using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Ina.Linker.Elf;
using Ina.Linker.MachO;

namespace Ina.Linker.Tests.Support;

/// <summary>
/// Reads a linked Mach-O executable the way the loader and the system tools do, so that tests can check the image
/// on any host: load commands, the fixup programs, the export trie, the symbol table and the code signature.
/// </summary>
internal sealed class MachOImage
{
    private readonly byte[] _data;

    public sealed record Section(string Segment, string Name, ulong Address, ulong Size, uint FileOffset, ulong Alignment, uint Flags, uint Reserved1, uint Reserved2)
    {
        public uint Type => Flags & MachSectionType.TypeMask;
    }

    public sealed record Segment(
        string Name, ulong Address, ulong VmSize, ulong FileOffset, ulong FileSize, uint MaxProtection, uint InitialProtection, uint Flags,
        IReadOnlyList<Section> Sections);

    public sealed record Library(string Name, bool IsWeak, Version CurrentVersion, Version CompatibilityVersion);

    public sealed record Symbol(string Name, byte Type, byte SectionNumber, ushort Desc, ulong Value)
    {
        public bool IsUndefined => (Type & MachSymbolType.TypeMask) == MachSymbolType.Undefined;

        public bool IsExternal => (Type & MachSymbolType.External) != 0;

        public int LibraryOrdinal => Desc >> 8;
    }

    public sealed record Bind(ulong Address, int LibraryOrdinal, string SymbolName, long Addend, bool IsWeakImport);

    /// <summary>The decoded compact unwind table (<c>__TEXT,__unwind_info</c>).</summary>
    /// <param name="Entries">Start address and encoding of every described range, in table order.</param>
    /// <param name="Lsdas">Address of the language specific data by function address.</param>
    /// <param name="PersonalitySlots">Addresses of the GOT entries that hold the personality functions.</param>
    /// <param name="End">The address behind the last described range.</param>
    public sealed record UnwindTable(
        List<(ulong Address, uint Encoding)> Entries, Dictionary<ulong, ulong> Lsdas, List<ulong> PersonalitySlots, ulong End)
    {
        /// <summary>The encoding that applies to <paramref name="address"/>, as the unwinder finds it.</summary>
        public uint EncodingAt(ulong address) => Entries.Last(entry => entry.Address <= address).Encoding;
    }

    public sealed record CodeSignature(string Identifier, uint Flags, uint CodeLimit, ulong ExecutableSegmentBase, ulong ExecutableSegmentLimit, bool HashesMatch);

    public MachOImage(byte[] data)
    {
        _data = data;
        Assert.Equal(MachHeader.Magic64, LittleEndian.U32(data, 0));
        CpuType = LittleEndian.U32(data, 4);
        CpuSubtype = LittleEndian.U32(data, 8);
        FileType = LittleEndian.U32(data, 12);
        Flags = LittleEndian.U32(data, 24);

        var commandCount = (int)LittleEndian.U32(data, 16);
        var offset = MachHeader.Size;
        for (var i = 0; i < commandCount; i++)
        {
            var command = LittleEndian.U32(data, offset);
            var size = (int)LittleEndian.U32(data, offset + 4);
            ReadCommand(command, data.AsSpan(offset, size));
            Commands.Add(command);
            offset += size;
        }

        Assert.Equal(MachHeader.Size + (int)LittleEndian.U32(data, 20), offset);
    }

    public uint CpuType { get; }

    public uint CpuSubtype { get; }

    public uint FileType { get; }

    public uint Flags { get; }

    public List<uint> Commands { get; } = [];

    public List<Segment> Segments { get; } = [];

    public List<Library> Libraries { get; } = [];

    public string? DynamicLoader { get; private set; }

    public byte[]? Uuid { get; private set; }

    public uint Platform { get; private set; }

    public Version? MinimumOSVersion { get; private set; }

    public Version? SdkVersion { get; private set; }

    /// <summary>The address at which execution starts (<c>LC_MAIN</c>).</summary>
    public ulong EntryAddress { get; private set; }

    public List<Symbol> SymbolTable { get; } = [];

    public int LocalSymbolCount { get; private set; }

    public int ExportedSymbolCount { get; private set; }

    public int UndefinedSymbolCount { get; private set; }

    public uint[] IndirectSymbols { get; private set; } = [];

    /// <summary>Addresses of the pointers that the loader adjusts by the load address.</summary>
    public List<ulong> Rebases { get; } = [];

    public List<Bind> Binds { get; } = [];

    /// <summary>The exported symbols with their addresses, as found by walking the export trie.</summary>
    public Dictionary<string, ulong> Exports { get; } = new(StringComparer.Ordinal);

    public List<ulong> FunctionStarts { get; } = [];

    public CodeSignature? Signature { get; private set; }

    public IEnumerable<Section> Sections => Segments.SelectMany(static segment => segment.Sections);

    /// <summary>The addresses of the defined symbols in the symbol table, by name.</summary>
    public Dictionary<string, ulong> Symbols =>
        SymbolTable.Where(static symbol => !symbol.IsUndefined).GroupBy(static symbol => symbol.Name).ToDictionary(static group => group.Key, static group => group.First().Value);

    public IEnumerable<string> UndefinedSymbols => SymbolTable.Where(static symbol => symbol.IsUndefined).Select(static symbol => symbol.Name);

    public Segment GetSegment(string name) => Segments.Single(segment => segment.Name == name);

    public Section GetSection(string segment, string name) => Sections.Single(section => section.Segment == segment && section.Name == name);

    public Section? FindSection(string segment, string name) => Sections.SingleOrDefault(section => section.Segment == segment && section.Name == name);

    /// <summary>The library that a bind takes its symbol from.</summary>
    public string LibraryOf(Bind bind) => Libraries[bind.LibraryOrdinal - 1].Name;

    /// <summary>Reads bytes at an address of the image.</summary>
    public ReadOnlySpan<byte> ReadAt(ulong address, int length)
    {
        foreach (var segment in Segments)
        {
            if (address >= segment.Address && address + (ulong)length <= segment.Address + segment.FileSize)
            {
                return _data.AsSpan((int)(segment.FileOffset + (address - segment.Address)), length);
            }
        }

        throw new ArgumentOutOfRangeException(nameof(address), $"0x{address:x} is not backed by the file");
    }

    public ReadOnlySpan<byte> ReadSection(Section section) => _data.AsSpan((int)section.FileOffset, (int)section.Size);

    public uint U32(ulong address) => LittleEndian.U32(ReadAt(address, 4), 0);

    public ulong U64(ulong address) => LittleEndian.U64(ReadAt(address, 8), 0);

    /// <summary>Decodes the compact unwind table the way the unwinder walks it.</summary>
    public UnwindTable ReadUnwindTable()
    {
        var section = GetSection("__TEXT", "__unwind_info");
        var data = ReadSection(section);
        var imageBase = GetSegment("__TEXT").Address;
        Assert.Equal(1u, LittleEndian.U32(data, 0));
        var commonOffset = (int)LittleEndian.U32(data, 4);
        var commonCount = (int)LittleEndian.U32(data, 8);
        var personalityOffset = (int)LittleEndian.U32(data, 12);
        var personalityCount = (int)LittleEndian.U32(data, 16);
        var indexOffset = (int)LittleEndian.U32(data, 20);
        var indexCount = (int)LittleEndian.U32(data, 24);

        var personalities = new List<ulong>();
        for (var i = 0; i < personalityCount; i++)
        {
            personalities.Add(imageBase + LittleEndian.U32(data, personalityOffset + i * 4));
        }

        var entries = new List<(ulong, uint)>();
        var lsdas = new Dictionary<ulong, ulong>();
        for (var i = 0; i + 1 < indexCount; i++)
        {
            var index = data.Slice(indexOffset + i * 12, 24);
            var pageFunction = LittleEndian.U32(index, 0);
            var page = data[(int)LittleEndian.U32(index, 4)..];

            // The language specific data of the functions of a page is a range of the LSDA index.
            for (var lsda = (int)LittleEndian.U32(index, 8); lsda < (int)LittleEndian.U32(index, 20); lsda += 8)
            {
                lsdas.Add(imageBase + LittleEndian.U32(data, lsda), imageBase + LittleEndian.U32(data, lsda + 4));
            }

            var entryOffset = LittleEndian.U16(page, 4);
            var entryCount = LittleEndian.U16(page, 6);
            if (LittleEndian.U32(page, 0) == CompactUnwind.SecondLevelRegular)
            {
                for (var e = 0; e < entryCount; e++)
                {
                    entries.Add((imageBase + LittleEndian.U32(page, entryOffset + e * 8), LittleEndian.U32(page, entryOffset + e * 8 + 4)));
                }

                continue;
            }

            Assert.Equal(CompactUnwind.SecondLevelCompressed, LittleEndian.U32(page, 0));
            var encodingsOffset = LittleEndian.U16(page, 8);
            for (var e = 0; e < entryCount; e++)
            {
                var entry = LittleEndian.U32(page, entryOffset + e * 4);
                var encodingIndex = (int)(entry >> 24);
                var encoding = encodingIndex < commonCount
                    ? LittleEndian.U32(data, commonOffset + encodingIndex * 4)
                    : LittleEndian.U32(page, encodingsOffset + (encodingIndex - commonCount) * 4);
                entries.Add((imageBase + pageFunction + (entry & 0xffffff), encoding));
            }
        }

        var sentinel = data.Slice(indexOffset + (indexCount - 1) * 12, 12);
        Assert.Equal(0u, LittleEndian.U32(sentinel, 4));
        return new UnwindTable(entries, lsdas, personalities, imageBase + LittleEndian.U32(sentinel, 0));
    }

    private static string FixedName(ReadOnlySpan<byte> field)
    {
        var length = field.IndexOf((byte)0);
        return Encoding.ASCII.GetString(length < 0 ? field : field[..length]);
    }

    private static Version DecodeVersion(uint value) => new((int)(value >> 16), (int)((value >> 8) & 0xff), (int)(value & 0xff));

    private void ReadCommand(uint command, ReadOnlySpan<byte> body)
    {
        switch (command)
        {
            case LoadCommand.Segment64:
            {
                var sections = new List<Section>();
                var count = (int)LittleEndian.U32(body, 64);
                var segmentName = FixedName(body.Slice(8, 16));
                for (var i = 0; i < count; i++)
                {
                    var header = body.Slice(LoadCommand.SegmentSize + i * LoadCommand.SectionSize, LoadCommand.SectionSize);
                    Assert.Equal(segmentName, FixedName(header.Slice(16, 16)));
                    sections.Add(new Section(
                        segmentName, FixedName(header[..16]), LittleEndian.U64(header, 32), LittleEndian.U64(header, 40), LittleEndian.U32(header, 48),
                        1UL << (int)LittleEndian.U32(header, 52), LittleEndian.U32(header, 64), LittleEndian.U32(header, 68), LittleEndian.U32(header, 72)));
                }

                Segments.Add(new Segment(
                    segmentName, LittleEndian.U64(body, 24), LittleEndian.U64(body, 32), LittleEndian.U64(body, 40), LittleEndian.U64(body, 48),
                    LittleEndian.U32(body, 56), LittleEndian.U32(body, 60), LittleEndian.U32(body, 68), sections));
                break;
            }

            case LoadCommand.LoadDylib or LoadCommand.LoadWeakDylib:
                Libraries.Add(new Library(
                    LittleEndian.CString(body, (int)LittleEndian.U32(body, 8)), command == LoadCommand.LoadWeakDylib, DecodeVersion(LittleEndian.U32(body, 16)),
                    DecodeVersion(LittleEndian.U32(body, 20))));
                break;

            case LoadCommand.LoadDylinker:
                DynamicLoader = LittleEndian.CString(body, (int)LittleEndian.U32(body, 8));
                break;

            case LoadCommand.Uuid:
                Uuid = body.Slice(8, 16).ToArray();
                break;

            case LoadCommand.BuildVersion:
                Platform = LittleEndian.U32(body, 8);
                MinimumOSVersion = DecodeVersion(LittleEndian.U32(body, 12));
                SdkVersion = DecodeVersion(LittleEndian.U32(body, 16));
                break;

            case LoadCommand.Main:
                EntryAddress = GetSegment("__TEXT").Address + LittleEndian.U64(body, 8);
                break;

            case LoadCommand.SymTab:
            {
                var symbolOffset = (int)LittleEndian.U32(body, 8);
                var count = (int)LittleEndian.U32(body, 12);
                var strings = _data.AsSpan((int)LittleEndian.U32(body, 16), (int)LittleEndian.U32(body, 20));
                for (var i = 0; i < count; i++)
                {
                    var entry = _data.AsSpan(symbolOffset + i * MachSymbolType.EntrySize, MachSymbolType.EntrySize);
                    SymbolTable.Add(new Symbol(
                        LittleEndian.CString(strings, (int)LittleEndian.U32(entry, 0)), entry[4], entry[5], LittleEndian.U16(entry, 6), LittleEndian.U64(entry, 8)));
                }

                break;
            }

            case LoadCommand.DySymTab:
            {
                Assert.Equal(0u, LittleEndian.U32(body, 8));
                LocalSymbolCount = (int)LittleEndian.U32(body, 12);
                Assert.Equal((uint)LocalSymbolCount, LittleEndian.U32(body, 16));
                ExportedSymbolCount = (int)LittleEndian.U32(body, 20);
                Assert.Equal((uint)(LocalSymbolCount + ExportedSymbolCount), LittleEndian.U32(body, 24));
                UndefinedSymbolCount = (int)LittleEndian.U32(body, 28);
                var indirectOffset = (int)LittleEndian.U32(body, 56);
                IndirectSymbols = new uint[LittleEndian.U32(body, 60)];
                for (var i = 0; i < IndirectSymbols.Length; i++)
                {
                    IndirectSymbols[i] = LittleEndian.U32(_data, indirectOffset + i * 4);
                }

                break;
            }

            case LoadCommand.DyldInfoOnly:
                ReadRebases(_data.AsSpan((int)LittleEndian.U32(body, 8), (int)LittleEndian.U32(body, 12)));
                ReadBinds(_data.AsSpan((int)LittleEndian.U32(body, 16), (int)LittleEndian.U32(body, 20)));
                Assert.Equal(0u, LittleEndian.U32(body, 28)); // weak binds
                Assert.Equal(0u, LittleEndian.U32(body, 36)); // lazy binds
                var exports = _data.AsSpan((int)LittleEndian.U32(body, 40), (int)LittleEndian.U32(body, 44));
                if (exports.Length > 0)
                {
                    ReadExports(exports, 0, string.Empty);
                }

                break;

            case LoadCommand.FunctionStarts:
            {
                var table = _data.AsSpan((int)LittleEndian.U32(body, 8), (int)LittleEndian.U32(body, 12));
                var address = GetSegment("__TEXT").Address;
                for (var position = 0; position < table.Length;)
                {
                    var delta = ReadUleb(table, ref position);
                    if (delta == 0 && FunctionStarts.Count > 0)
                    {
                        break;
                    }

                    address += delta;
                    FunctionStarts.Add(address);
                }

                break;
            }

            case LoadCommand.CodeSignature:
                ReadSignature((int)LittleEndian.U32(body, 8), (int)LittleEndian.U32(body, 12));
                break;
        }
    }

    private static ulong ReadUleb(ReadOnlySpan<byte> data, ref int position)
    {
        var result = 0UL;
        for (var shift = 0; ; shift += 7)
        {
            var value = data[position++];
            result |= (ulong)(value & 0x7f) << shift;
            if ((value & 0x80) == 0)
            {
                return result;
            }
        }
    }

    private static long ReadSleb(ReadOnlySpan<byte> data, ref int position)
    {
        var result = 0L;
        var shift = 0;
        byte value;
        do
        {
            value = data[position++];
            result |= (long)(value & 0x7f) << shift;
            shift += 7;
        }
        while ((value & 0x80) != 0);

        return shift < 64 && (value & 0x40) != 0 ? result | (-1L << shift) : result;
    }

    /// <summary>Runs the rebase program the way the loader does and records every pointer it touches.</summary>
    private void ReadRebases(ReadOnlySpan<byte> program)
    {
        var segment = 0;
        var offset = 0UL;
        for (var position = 0; position < program.Length;)
        {
            var value = program[position++];
            var immediate = value & DyldInfo.ImmediateMask;
            switch (value & DyldInfo.OpcodeMask)
            {
                case DyldInfo.RebaseDone:
                    return;
                case DyldInfo.RebaseSetTypeImm:
                    Assert.Equal(DyldInfo.TypePointer, immediate);
                    break;
                case DyldInfo.RebaseSetSegmentAndOffsetUleb:
                    segment = immediate;
                    offset = ReadUleb(program, ref position);
                    break;
                case DyldInfo.RebaseAddAddrUleb:
                    offset += ReadUleb(program, ref position);
                    break;
                case DyldInfo.RebaseAddAddrImmScaled:
                    offset += (ulong)immediate * 8;
                    break;
                case DyldInfo.RebaseDoRebaseImmTimes:
                    for (var i = 0; i < immediate; i++, offset += 8)
                    {
                        Rebases.Add(Segments[segment].Address + offset);
                    }

                    break;
                case DyldInfo.RebaseDoRebaseUlebTimes:
                    for (var count = ReadUleb(program, ref position); count > 0; count--, offset += 8)
                    {
                        Rebases.Add(Segments[segment].Address + offset);
                    }

                    break;
                case DyldInfo.RebaseDoRebaseAddAddrUleb:
                    Rebases.Add(Segments[segment].Address + offset);
                    offset += 8 + ReadUleb(program, ref position);
                    break;
                case DyldInfo.RebaseDoRebaseUlebTimesSkippingUleb:
                {
                    var count = ReadUleb(program, ref position);
                    var skip = ReadUleb(program, ref position);
                    for (; count > 0; count--, offset += 8 + skip)
                    {
                        Rebases.Add(Segments[segment].Address + offset);
                    }

                    break;
                }

                default:
                    Assert.Fail($"unknown rebase opcode 0x{value:x2}");
                    break;
            }
        }
    }

    /// <summary>Runs the bind program the way the loader does and records every pointer it fills in.</summary>
    private void ReadBinds(ReadOnlySpan<byte> program)
    {
        var segment = 0;
        var offset = 0UL;
        var ordinal = 0;
        var name = string.Empty;
        var weak = false;
        var addend = 0L;
        for (var position = 0; position < program.Length;)
        {
            var value = program[position++];
            var immediate = value & DyldInfo.ImmediateMask;
            switch (value & DyldInfo.OpcodeMask)
            {
                case DyldInfo.BindDone:
                    return;
                case DyldInfo.BindSetDylibOrdinalImm:
                    ordinal = immediate;
                    break;
                case DyldInfo.BindSetDylibOrdinalUleb:
                    ordinal = (int)ReadUleb(program, ref position);
                    break;
                case DyldInfo.BindSetSymbolTrailingFlagsImm:
                    name = LittleEndian.CString(program, position);
                    position += Encoding.UTF8.GetByteCount(name) + 1;
                    weak = (immediate & DyldInfo.BindSymbolFlagsWeakImport) != 0;
                    break;
                case DyldInfo.BindSetTypeImm:
                    Assert.Equal(DyldInfo.TypePointer, immediate);
                    break;
                case DyldInfo.BindSetAddendSleb:
                    addend = ReadSleb(program, ref position);
                    break;
                case DyldInfo.BindSetSegmentAndOffsetUleb:
                    segment = immediate;
                    offset = ReadUleb(program, ref position);
                    break;
                case DyldInfo.BindAddAddrUleb:
                    offset += ReadUleb(program, ref position);
                    break;
                case DyldInfo.BindDoBind:
                    Binds.Add(new Bind(Segments[segment].Address + offset, ordinal, name, addend, weak));
                    offset += 8;
                    break;
                case DyldInfo.BindDoBindAddAddrUleb:
                    Binds.Add(new Bind(Segments[segment].Address + offset, ordinal, name, addend, weak));
                    offset += 8 + ReadUleb(program, ref position);
                    break;
                case DyldInfo.BindDoBindAddAddrImmScaled:
                    Binds.Add(new Bind(Segments[segment].Address + offset, ordinal, name, addend, weak));
                    offset += 8 + (ulong)immediate * 8;
                    break;
                default:
                    Assert.Fail($"unknown bind opcode 0x{value:x2}");
                    break;
            }
        }
    }

    private void ReadExports(ReadOnlySpan<byte> trie, int nodeOffset, string prefix)
    {
        var position = nodeOffset;
        var terminalSize = (int)ReadUleb(trie, ref position);
        var children = position + terminalSize;
        if (terminalSize > 0)
        {
            Assert.Equal(0UL, ReadUleb(trie, ref position)); // flags: a regular symbol
            Exports[prefix] = Segments.First(static segment => segment.Name == "__TEXT").Address + ReadUleb(trie, ref position);
        }

        position = children;
        var childCount = trie[position++];
        for (var i = 0; i < childCount; i++)
        {
            var label = LittleEndian.CString(trie, position);
            position += Encoding.UTF8.GetByteCount(label) + 1;
            var child = (int)ReadUleb(trie, ref position);
            ReadExports(trie, child, prefix + label);
        }
    }

    /// <summary>Parses the embedded signature and recomputes the page hashes, as the kernel does when it maps a page.</summary>
    private void ReadSignature(int offset, int size)
    {
        var blob = _data.AsSpan(offset, size);
        Assert.Equal(0xfade0cc0u, BinaryPrimitives.ReadUInt32BigEndian(blob));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(blob[8..]));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(blob[12..]));
        var directory = blob[(int)BinaryPrimitives.ReadUInt32BigEndian(blob[16..])..];
        Assert.Equal(0xfade0c02u, BinaryPrimitives.ReadUInt32BigEndian(directory));

        var flags = BinaryPrimitives.ReadUInt32BigEndian(directory[12..]);
        var hashOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(directory[16..]);
        var identifierOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(directory[20..]);
        var slotCount = (int)BinaryPrimitives.ReadUInt32BigEndian(directory[28..]);
        var codeLimit = BinaryPrimitives.ReadUInt32BigEndian(directory[32..]);
        Assert.Equal(32, directory[36]);
        Assert.Equal(2, directory[37]);
        var pageSize = 1 << directory[39];

        var matches = slotCount == (codeLimit + pageSize - 1) / pageSize;
        Span<byte> hash = stackalloc byte[32];
        for (var i = 0; i < slotCount && matches; i++)
        {
            var start = i * pageSize;
            SHA256.HashData(_data.AsSpan(start, (int)Math.Min(pageSize, codeLimit - start)), hash);
            matches = hash.SequenceEqual(directory.Slice(hashOffset + i * 32, 32));
        }

        Signature = new CodeSignature(
            LittleEndian.CString(directory, identifierOffset), flags, codeLimit, BinaryPrimitives.ReadUInt64BigEndian(directory[64..]),
            BinaryPrimitives.ReadUInt64BigEndian(directory[72..]), matches && codeLimit == offset);
    }
}
