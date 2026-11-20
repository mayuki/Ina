
namespace Ina.Linker.Elf;

/// <summary>A relocation that the dynamic loader applies when the program starts.</summary>
internal sealed class DynamicRelocation
{
    /// <summary>The fragment that contains the patched location; null when it lives in <see cref="Chunk"/>.</summary>
    public Fragment? Fragment { get; init; }

    /// <summary>The linker-generated chunk that contains the patched location.</summary>
    public OutputChunk? Chunk { get; init; }

    /// <summary>Offset of the patched location inside the fragment or chunk.</summary>
    public required ulong Offset { get; init; }

    public required uint Type { get; init; }

    /// <summary>The dynamic symbol the loader looks up, for symbolic relocations.</summary>
    public Symbol? DynamicSymbol { get; init; }

    /// <summary>For relative relocations, the symbol whose link-time address is added to <see cref="Addend"/>.</summary>
    public Symbol? Target { get; init; }

    public long Addend { get; init; }

    public ulong Address => (Fragment?.Address ?? Chunk!.Address) + Offset;
}
