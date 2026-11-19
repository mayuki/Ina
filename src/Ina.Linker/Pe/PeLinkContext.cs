namespace Ina.Linker.Pe;

/// <summary>State shared between the linker passes and the chunks of the image.</summary>
internal sealed class PeLinkContext(PeLinkOptions options, PeTarget target)
{
    public PeTarget Target { get; } = target;

    /// <summary>The address the image is linked for. Windows moves the image when <see cref="IsRelocatable"/> is set.</summary>
    public ulong ImageBase { get; } = PeConstants.DefaultImageBase;

    public bool IsRelocatable { get; } = options.DynamicBase;

    /// <summary>Applies all relocations of <paramref name="section"/> to its copy in the output buffer.</summary>
    public void ApplyRelocations(CoffSection section, Span<byte> data)
    {
        var symbols = section.File.Symbols;
        foreach (ref readonly var relocation in section.Relocations.AsSpan())
        {
            Target.Apply(this, section, data, relocation, symbols[relocation.SymbolIndex].Target);
        }
    }
}

/// <summary>A chunk whose content the linker generates once all addresses are known.</summary>
internal sealed class PeGeneratedChunk(string sectionName, uint characteristics, uint alignment, uint size, Action<Span<byte>>? write = null)
    : PeChunk
{
    public override string SectionName => sectionName;

    public override uint Characteristics => characteristics;

    public override uint Alignment => alignment;

    public override uint Size => size;

    public override string DisplayName => $"<ina>:({sectionName})";

    public override void Write(Span<byte> destination, PeLinkContext context) => write?.Invoke(destination);
}
