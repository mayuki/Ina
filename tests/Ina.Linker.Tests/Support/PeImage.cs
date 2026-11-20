using System.Text;

namespace Ina.Linker.Tests.Support;

/// <summary>A minimal reader for Windows executables, used to inspect what the PE linker produced.</summary>
internal sealed class PeImage
{
    public const int DirectoryExport = 0;
    public const int DirectoryImport = 1;
    public const int DirectoryResource = 2;
    public const int DirectoryException = 3;
    public const int DirectoryBaseRelocation = 5;
    public const int DirectoryTls = 9;
    public const int DirectoryLoadConfig = 10;
    public const int DirectoryImportAddressTable = 12;

    private readonly byte[] _data;
    private readonly int _optionalHeader;

    public PeImage(byte[] data)
    {
        _data = data;
        Assert.Equal((byte)'M', data[0]);
        Assert.Equal((byte)'Z', data[1]);
        var header = (int)LittleEndian.U32(data, 0x3c);
        Assert.Equal(0x4550u, LittleEndian.U32(data, header));

        Machine = LittleEndian.U16(data, header + 4);
        int sectionCount = LittleEndian.U16(data, header + 6);
        TimeDateStamp = LittleEndian.U32(data, header + 8);
        int optionalHeaderSize = LittleEndian.U16(data, header + 20);
        Characteristics = LittleEndian.U16(data, header + 22);

        _optionalHeader = header + 24;
        Assert.Equal(0x20b, LittleEndian.U16(data, _optionalHeader));
        EntryPoint = LittleEndian.U32(data, _optionalHeader + 16);
        ImageBase = LittleEndian.U64(data, _optionalHeader + 24);
        SectionAlignment = LittleEndian.U32(data, _optionalHeader + 32);
        FileAlignment = LittleEndian.U32(data, _optionalHeader + 36);
        SizeOfImage = LittleEndian.U32(data, _optionalHeader + 56);
        SizeOfHeaders = LittleEndian.U32(data, _optionalHeader + 60);
        Subsystem = LittleEndian.U16(data, _optionalHeader + 68);
        DllCharacteristics = LittleEndian.U16(data, _optionalHeader + 70);
        StackReserve = LittleEndian.U64(data, _optionalHeader + 72);

        for (var i = 0; i < sectionCount; i++)
        {
            var h = data.AsSpan(_optionalHeader + optionalHeaderSize + i * 40, 40);
            var nameLength = h[..8].IndexOf((byte)0);
            Sections.Add(new Section(
                Encoding.ASCII.GetString(nameLength < 0 ? h[..8] : h[..nameLength]), LittleEndian.U32(h, 8), LittleEndian.U32(h, 12),
                LittleEndian.U32(h, 16), LittleEndian.U32(h, 20), LittleEndian.U32(h, 36)));
        }

        ReadImports();
        ReadExports();
        ReadBaseRelocations();
    }

    public sealed record Section(string Name, uint VirtualSize, uint Rva, uint RawSize, uint FileOffset, uint Characteristics);

    public ushort Machine { get; }

    public uint TimeDateStamp { get; }

    public ushort Characteristics { get; }

    public uint EntryPoint { get; }

    public ulong ImageBase { get; }

    public uint SectionAlignment { get; }

    public uint FileAlignment { get; }

    public uint SizeOfImage { get; }

    public uint SizeOfHeaders { get; }

    public ushort Subsystem { get; }

    public ushort DllCharacteristics { get; }

    public ulong StackReserve { get; }

    public List<Section> Sections { get; } = [];

    /// <summary>The imported DLLs in the order of the import directory, each with the names it provides.</summary>
    public List<(string Library, List<string> Names)> Imports { get; } = [];

    /// <summary>The slots of the import address table by their address, with the name the loader resolves for each.</summary>
    public Dictionary<uint, string> ImportSlots { get; } = [];

    /// <summary>
    /// Imported names by what their address slot holds in the file. A program that is run without a loader finds
    /// this value where the address of the function would be.
    /// </summary>
    public Dictionary<ulong, string> ImportNames { get; } = [];

    /// <summary>Exported names and their addresses relative to the image base.</summary>
    public Dictionary<string, uint> Exports { get; } = new(StringComparer.Ordinal);

    /// <summary>The addresses that the loader adjusts when it moves the image.</summary>
    public List<uint> BaseRelocations { get; } = [];

    public (uint Rva, uint Size) Directory(int index) =>
        (LittleEndian.U32(_data, _optionalHeader + 112 + index * 8), LittleEndian.U32(_data, _optionalHeader + 112 + index * 8 + 4));

    public Section? FindSection(string name) => Sections.Find(s => s.Name == name);

    public Section SectionOf(uint rva) =>
        Sections.Find(s => rva >= s.Rva && rva < s.Rva + Math.Max(s.VirtualSize, s.RawSize)) ?? throw new InvalidOperationException($"0x{rva:x} is not in any section");

    /// <summary>Reads bytes at an address relative to the image base; memory beyond the file content reads as zero.</summary>
    public byte[] Read(uint rva, int length)
    {
        var section = SectionOf(rva);
        var result = new byte[length];
        var offset = rva - section.Rva;
        var available = (int)Math.Min(length, Math.Max(0, (long)section.RawSize - offset));
        _data.AsSpan((int)(section.FileOffset + offset), available).CopyTo(result);
        return result;
    }

    public uint U32(uint rva) => LittleEndian.U32(Read(rva, 4), 0);

    public ulong U64(uint rva) => LittleEndian.U64(Read(rva, 8), 0);

    public string CString(uint rva)
    {
        var section = SectionOf(rva);
        return LittleEndian.CString(_data, (int)(section.FileOffset + rva - section.Rva));
    }

    /// <summary>Whether the file contains the given bytes anywhere.</summary>
    public bool Contains(ReadOnlySpan<byte> marker) => _data.AsSpan().IndexOf(marker) >= 0;

    private void ReadImports()
    {
        var (rva, _) = Directory(DirectoryImport);
        if (rva == 0)
        {
            return;
        }

        for (; U32(rva + 12) != 0; rva += 20)
        {
            var names = new List<string>();
            var address = U32(rva + 16);
            for (var slot = U32(rva); U64(slot) != 0; slot += 8, address += 8)
            {
                // Skip the two-byte hint in front of the name.
                names.Add(CString((uint)U64(slot) + 2));
                ImportSlots[address] = names[^1];
                ImportNames[U64(address)] = names[^1];
            }

            Imports.Add((CString(U32(rva + 12)), names));
        }
    }

    private void ReadExports()
    {
        var (rva, _) = Directory(DirectoryExport);
        if (rva == 0)
        {
            return;
        }

        var count = U32(rva + 24);
        var functions = U32(rva + 28);
        var names = U32(rva + 32);
        var ordinals = U32(rva + 36);
        for (uint i = 0; i < count; i++)
        {
            var ordinal = LittleEndian.U16(Read(ordinals + i * 2, 2), 0);
            Exports[CString(U32(names + i * 4))] = U32(functions + ordinal * 4u);
        }
    }

    private void ReadBaseRelocations()
    {
        var (rva, size) = Directory(DirectoryBaseRelocation);
        for (var end = rva + size; rva < end;)
        {
            var page = U32(rva);
            var blockSize = U32(rva + 4);
            for (uint entry = 8; entry < blockSize; entry += 2)
            {
                var value = LittleEndian.U16(Read(rva + entry, 2), 0);
                if (value >> 12 != 0)
                {
                    // Type 10 is a 64-bit address; type 0 only pads the block.
                    Assert.Equal(10, value >> 12);
                    BaseRelocations.Add(page + (value & 0xfffu));
                }
            }

            rva += blockSize;
        }
    }
}
