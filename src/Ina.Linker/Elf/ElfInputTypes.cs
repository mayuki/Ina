
namespace Ina.Linker.Elf;

/// <summary>A raw ELF symbol table entry together with its resolved name.</summary>
/// <param name="Section">The index of the defining section, or -1 when the symbol is undefined, absolute or common.</param>
internal readonly record struct ElfSymbol(string Name, byte Info, byte Other, ushort RawSectionIndex, int Section, ulong Value, ulong Size)
{
    public byte Binding => (byte)(Info >> 4);

    public byte Type => (byte)(Info & 0xf);

    public byte Visibility => (byte)(Other & 0x3);

    public bool IsUndefined => RawSectionIndex == SectionIndex.Undefined;

    public bool IsAbsolute => RawSectionIndex == SectionIndex.Abs;

    public bool IsCommon => RawSectionIndex == SectionIndex.Common;

    public bool IsLocal => Binding == SymbolBinding.Local;
}

/// <summary>A relocation with an explicit addend (ELF64 RELA).</summary>
internal readonly record struct Relocation(ulong Offset, uint Type, uint SymbolIndex, long Addend);

/// <summary>
/// A run of bytes from an input file that is placed into the output and may carry relocations.
/// Regular input sections and the individual records of <c>.eh_frame</c> are both fragments.
/// </summary>
internal abstract class Fragment
{
    protected Fragment(ElfObjectFile file)
    {
        File = file;
    }

    /// <summary>The object file this fragment came from; relocation symbol indices are relative to it.</summary>
    public ElfObjectFile File { get; }

    public Relocation[] Relocations { get; set; } = [];

    /// <summary>Whether the fragment survived garbage collection and is part of the output.</summary>
    public bool IsLive { get; set; }

    /// <summary>The address of the fragment in the output image. Valid after layout.</summary>
    public abstract ulong Address { get; }

    public abstract ReadOnlySpan<byte> Content { get; }

    /// <summary>Whether the fragment ends up in a writable segment (dynamic relocations are only allowed there).</summary>
    public abstract bool IsWritable { get; }

    public abstract string DisplayName { get; }
}
