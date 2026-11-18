using Ina.Linker.Output;

namespace Ina.Linker.Input;

/// <summary>
/// One CIE or FDE of an input <c>.eh_frame</c> section. Records are handled individually so that FDEs describing
/// discarded code can be dropped and so that the <c>.eh_frame_hdr</c> lookup table can be generated.
/// </summary>
internal sealed class EhFrameRecord : Fragment
{
    private readonly ReadOnlyMemory<byte> _content;

    public EhFrameRecord(ObjectFile file, ReadOnlyMemory<byte> content, bool isCie)
        : base(file)
    {
        _content = content;
        IsCie = isCie;
    }

    public bool IsCie { get; }

    /// <summary>For an FDE, the CIE it refers to.</summary>
    public EhFrameRecord? Cie { get; set; }

    /// <summary>For an FDE, the code section it describes. Null when that section was discarded.</summary>
    public InputSection? Target { get; set; }

    /// <summary>For an FDE, the relocation that yields the start address of the described code.</summary>
    public Relocation? PcBeginRelocation { get; set; }

    /// <summary>The synthesized output <c>.eh_frame</c> section. Null until layout.</summary>
    public OutputChunk? Parent { get; set; }

    /// <summary>Offset of this record inside the output <c>.eh_frame</c>.</summary>
    public ulong Offset { get; set; }

    public int Size => _content.Length;

    public override ulong Address => Parent is null ? 0 : Parent.Address + Offset;

    public override ReadOnlySpan<byte> Content => _content.Span;

    public override bool IsWritable => false;

    public override string DisplayName => $"{File.Name}:(.eh_frame)";
}
