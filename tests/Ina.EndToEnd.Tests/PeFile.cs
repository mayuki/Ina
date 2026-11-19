using System.Buffers.Binary;
using System.Text;

namespace Ina.EndToEnd.Tests;

/// <summary>The few facts about a Windows executable that the tests check without running it.</summary>
/// <param name="IsRelocatable">Whether Windows may load the image at any address (<c>IMAGE_DLLCHARACTERISTICS_DYNAMIC_BASE</c>).</param>
/// <param name="Exports">The names in the export table.</param>
internal sealed record PeFile(ushort Machine, ushort Subsystem, bool IsRelocatable, IReadOnlyList<string> Exports)
{
    /// <summary>IMAGE_FILE_MACHINE_AMD64.</summary>
    public const ushort MachineX64 = 0x8664;

    /// <summary>IMAGE_FILE_MACHINE_ARM64.</summary>
    public const ushort MachineArm64 = 0xaa64;

    /// <summary>IMAGE_SUBSYSTEM_WINDOWS_GUI: a graphical application, started without a console window.</summary>
    public const ushort SubsystemWindows = 2;

    /// <summary>IMAGE_SUBSYSTEM_WINDOWS_CUI: a console application.</summary>
    public const ushort SubsystemConsole = 3;

    private const ushort DynamicBase = 0x0040;

    public static PeFile Read(string path)
    {
        var data = File.ReadAllBytes(path);
        Assert.True(data.Length > 0x40 && data.AsSpan(0, 2).SequenceEqual("MZ"u8), $"{path} is not a Windows executable.");

        // The DOS header points to the PE signature; the file header follows it, then the optional header of PE32+
        // with the subsystem at 68, the DLL characteristics at 70 and the data directories from 112 on.
        var header = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x3c));
        Assert.True(data.AsSpan(header, 4).SequenceEqual("PE\0\0"u8), $"{path} has no PE signature.");
        var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(header + 6));
        var optionalHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(header + 20));
        var optionalHeader = header + 24;
        var sections = optionalHeader + optionalHeaderSize;

        // A virtual address is found in the file through the section that contains it.
        int FileOffset(uint rva)
        {
            for (var i = 0; i < sectionCount; i++)
            {
                var section = data.AsSpan(sections + (i * 40));
                var start = BinaryPrimitives.ReadUInt32LittleEndian(section[12..]);
                var size = Math.Max(BinaryPrimitives.ReadUInt32LittleEndian(section[8..]), BinaryPrimitives.ReadUInt32LittleEndian(section[16..]));
                if (rva >= start && rva < start + size)
                {
                    return (int)(rva - start + BinaryPrimitives.ReadUInt32LittleEndian(section[20..]));
                }
            }

            throw new InvalidDataException($"The address 0x{rva:x} of {path} is in no section.");
        }

        // The export directory lists the number of names at 24 and the table of their addresses at 32.
        var exports = new List<string>();
        var exportDirectory = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(optionalHeader + 112));
        if (exportDirectory != 0)
        {
            var directory = FileOffset(exportDirectory);
            var nameCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(directory + 24));
            var names = FileOffset(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(directory + 32)));
            for (var i = 0; i < nameCount; i++)
            {
                var name = FileOffset(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(names + (i * 4))));
                exports.Add(Encoding.ASCII.GetString(data, name, data.AsSpan(name).IndexOf((byte)0)));
            }
        }

        return new PeFile(
            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(header + 4)),
            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(optionalHeader + 68)),
            (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(optionalHeader + 70)) & DynamicBase) != 0,
            exports);
    }
}
