using System.Buffers.Binary;

namespace Ina.EndToEnd.Tests;

/// <summary>The few facts about an ELF file that the tests check without running it.</summary>
internal sealed record ElfFile(ushort Type, ushort Machine, bool HasSymbolTable)
{
    /// <summary>e_type of a fixed-address executable.</summary>
    public const ushort TypeExecutable = 2;

    /// <summary>e_type of a position independent executable.</summary>
    public const ushort TypeSharedObject = 3;

    /// <summary>e_machine of AArch64.</summary>
    public const ushort MachineAArch64 = 183;

    private const uint SectionTypeSymbolTable = 2;

    public static ElfFile Read(string path)
    {
        var data = File.ReadAllBytes(path);
        Assert.True(data.Length > 64 && data.AsSpan(0, 4).SequenceEqual("\u007fELF"u8), $"{path} is not an ELF file.");

        // 64-bit little-endian header: e_shoff at 0x28, e_shentsize at 0x3a, e_shnum at 0x3c; sh_type at offset 4 of an entry.
        var sectionHeaders = (int)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x28));
        var entrySize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x3a));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x3c));
        var hasSymbolTable = Enumerable.Range(0, count)
            .Any(i => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(sectionHeaders + (i * entrySize) + 4)) == SectionTypeSymbolTable);

        return new ElfFile(
            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(16)),
            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(18)),
            hasSymbolTable);
    }
}
