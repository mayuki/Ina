
namespace Ina.Linker.Elf;

/// <summary>An allocatable section of an input object file.</summary>
internal sealed class InputSection : Fragment
{
    private readonly ReadOnlyMemory<byte> _content;

    public InputSection(ElfObjectFile file, int index, string name, uint type, ulong flags, ulong size, ulong alignment, ReadOnlyMemory<byte> content)
        : base(file)
    {
        Index = index;
        Name = name;
        Type = type;
        Flags = flags;
        Size = size;
        Alignment = Math.Max(alignment, 1);
        _content = content;
    }

    /// <summary>The section index inside the object file, or -1 for linker-synthesized sections.</summary>
    public int Index { get; }

    public string Name { get; }

    public uint Type { get; }

    public ulong Flags { get; }

    public ulong Size { get; }

    public ulong Alignment { get; }

    /// <summary>The output section this input section was assigned to. Null until layout.</summary>
    public OutputSection? Parent { get; set; }

    /// <summary>Offset of this section inside <see cref="Parent"/>.</summary>
    public ulong Offset { get; set; }

    public bool IsNoBits => Type == SectionType.NoBits;

    public bool IsTls => (Flags & SectionFlags.Tls) != 0;

    public bool IsExecutable => (Flags & SectionFlags.ExecInstr) != 0;

    public override bool IsWritable => (Flags & SectionFlags.Write) != 0;

    public override ulong Address => Parent is null ? 0 : Parent.Address + Offset;

    public override ReadOnlySpan<byte> Content => _content.Span;

    public override string DisplayName => $"{File.Name}:({Name})";

    public override string ToString() => DisplayName;
}
