using System.Buffers.Binary;
using System.Text;

namespace Ina.Linker.Elf;

/// <summary>Little-endian accessors. Every target Ina supports is little-endian, regardless of the host.</summary>
internal static class LittleEndian
{
    public static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);

    public static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);

    public static int I32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);

    public static ulong U64(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);

    public static long I64(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadInt64LittleEndian(data[offset..]);

    public static void U16(Span<byte> data, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data[offset..], value);

    public static void U32(Span<byte> data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data[offset..], value);

    public static void I32(Span<byte> data, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(data[offset..], value);

    public static void U64(Span<byte> data, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(data[offset..], value);

    public static void I64(Span<byte> data, int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(data[offset..], value);

    /// <summary>Reads a NUL-terminated string starting at <paramref name="offset"/>.</summary>
    public static string CString(ReadOnlySpan<byte> data, int offset)
    {
        if ((uint)offset >= (uint)data.Length)
        {
            return string.Empty;
        }

        var rest = data[offset..];
        var length = rest.IndexOf((byte)0);
        return Encoding.UTF8.GetString(length < 0 ? rest : rest[..length]);
    }
}
