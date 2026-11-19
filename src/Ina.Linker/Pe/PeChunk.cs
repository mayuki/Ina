namespace Ina.Linker.Pe;

/// <summary>
/// A run of bytes that is placed into the output image: a section of an input object, or a table the linker
/// generates itself (imports, exports, base relocations).
/// </summary>
internal abstract class PeChunk
{
    /// <summary>
    /// The name that decides where the chunk goes. The part before <c>$</c> selects the output section, and the
    /// chunks of an output section are ordered by their full names.
    /// </summary>
    public abstract string SectionName { get; }

    /// <summary>Content and protection flags (<see cref="CoffSectionFlags"/>).</summary>
    public abstract uint Characteristics { get; }

    public abstract uint Alignment { get; }

    public abstract uint Size { get; }

    public abstract string DisplayName { get; }

    /// <summary>Whether the chunk is part of the output. Chunks that nothing refers to are dropped.</summary>
    public bool IsLive { get; set; }

    /// <summary>The output section the chunk was assigned to. Null until layout.</summary>
    public PeOutputSection? Output { get; set; }

    /// <summary>The address of the chunk relative to the image base. Valid after layout.</summary>
    public uint Rva { get; set; }

    /// <summary>Zero-initialized chunks take up address space but no room in the file.</summary>
    public bool IsUninitialized => (Characteristics & CoffSectionFlags.UninitializedData) != 0;

    /// <summary>Writes the final content of the chunk; <paramref name="destination"/> is zero-filled and has <see cref="Size"/> bytes.</summary>
    public abstract void Write(Span<byte> destination, PeLinkContext context);

    public override string ToString() => DisplayName;
}

/// <summary>A section of the output image, made of the chunks whose names share the part before <c>$</c>.</summary>
internal sealed class PeOutputSection(string name)
{
    public string Name { get; } = name;

    public List<PeChunk> Chunks { get; } = [];

    public uint Characteristics { get; set; }

    public uint Rva { get; set; }

    /// <summary>The size in memory, including zero-initialized data at the end.</summary>
    public uint VirtualSize { get; set; }

    /// <summary>The number of bytes stored in the file, before rounding up to the file alignment.</summary>
    public uint RawSize { get; set; }

    public uint FileOffset { get; set; }

    /// <summary>The one-based position in the section table, which <c>SECTION</c> relocations refer to.</summary>
    public int Index { get; set; }

    public override string ToString() => Name;
}
