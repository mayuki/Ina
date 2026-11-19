using System.Buffers.Binary;

namespace Ina.EndToEnd.Tests;

/// <summary>The few facts about a Mach-O file that the tests check without running it.</summary>
/// <param name="LocalSymbolCount">The symbols that are neither exported nor imported; a stripped image has none.</param>
internal sealed record MachOFile(uint CpuType, int LocalSymbolCount)
{
    /// <summary>CPU_TYPE_ARM64.</summary>
    public const uint CpuTypeArm64 = 0x0100000c;

    /// <summary>CPU_TYPE_X86_64.</summary>
    public const uint CpuTypeX64 = 0x01000007;

    private const uint Magic64 = 0xfeedfacf;
    private const int HeaderSize = 32;
    private const uint LoadCommandDynamicSymbolTable = 0xb;

    public static MachOFile Read(string path)
    {
        var data = File.ReadAllBytes(path);
        Assert.True(data.Length > HeaderSize && BinaryPrimitives.ReadUInt32LittleEndian(data) == Magic64, $"{path} is not a 64-bit Mach-O file.");

        // mach_header_64: ncmds at 16. A load command starts with its type and size; LC_DYSYMTAB continues with
        // ilocalsym and nlocalsym.
        var commandCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));
        var localSymbolCount = 0;
        var offset = HeaderSize;
        for (var i = 0; i < commandCount; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)) == LoadCommandDynamicSymbolTable)
            {
                localSymbolCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 12));
            }

            offset += (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
        }

        return new MachOFile(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)), localSymbolCount);
    }
}
