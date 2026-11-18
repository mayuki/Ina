using System.Text;
using Ina.Linker.Elf;

namespace Ina.Linker.Output;

/// <summary>A chunk whose bytes are known up front.</summary>
internal sealed class DataChunk : OutputChunk
{
    private readonly byte[] _data;

    public DataChunk(string name, uint type, ulong flags, ulong alignment, byte[] data)
        : base(name, type, flags, alignment)
    {
        _data = data;
        Size = (ulong)data.Length;
    }

    public override void Write(Span<byte> destination, LinkContext context) => _data.CopyTo(destination);
}

/// <summary>A string table (<c>.dynstr</c>, <c>.strtab</c>, <c>.shstrtab</c>).</summary>
internal sealed class StringTableChunk : OutputChunk
{
    private readonly MemoryStream _data = new();
    private readonly Dictionary<string, uint> _offsets = new(StringComparer.Ordinal);

    public StringTableChunk(string name, ulong flags)
        : base(name, SectionType.StrTab, flags, 1)
    {
        // Offset 0 is the empty string by convention.
        _data.WriteByte(0);
        _offsets[string.Empty] = 0;
        Size = 1;
    }

    public uint Add(string value)
    {
        if (_offsets.TryGetValue(value, out var offset))
        {
            return offset;
        }

        offset = (uint)_data.Length;
        _data.Write(Encoding.UTF8.GetBytes(value));
        _data.WriteByte(0);
        _offsets[value] = offset;
        Size = (ulong)_data.Length;
        return offset;
    }

    public override void Write(Span<byte> destination, LinkContext context) => _data.GetBuffer().AsSpan(0, (int)_data.Length).CopyTo(destination);
}
