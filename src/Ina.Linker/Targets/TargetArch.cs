using Ina.Linker.Elf;
using Ina.Linker.Input;
using Ina.Linker.Symbols;

namespace Ina.Linker.Targets;

/// <summary>A relocation in linker-generated code, expressed against a symbol name.</summary>
internal readonly record struct SyntheticRelocation(int Offset, uint Type, string Symbol, long Addend);

/// <summary>
/// A function whose machine code the linker generates itself. These stand in for the pieces a native toolchain
/// links statically into every program: the startup objects (crt1.o and friends) and <c>libc_nonshared.a</c>.
/// </summary>
internal sealed record SyntheticFunction(string Name, byte[] Code, SyntheticRelocation[] Relocations);

/// <summary>The architecture specific parts of the linker.</summary>
internal abstract class TargetArch
{
    public abstract ElfMachine Machine { get; }

    /// <summary>The largest page size the image must be loadable with; segments are aligned to it.</summary>
    public abstract ulong PageSize { get; }

    /// <summary>Load address of fixed-address (non-PIE) executables.</summary>
    public abstract ulong DefaultImageBase { get; }

    public abstract int PltEntrySize { get; }

    public abstract uint RelativeRelocation { get; }

    public abstract uint GlobDatRelocation { get; }

    public abstract uint Abs64Relocation { get; }

    /// <summary>
    /// Called once before relocations are scanned, for decisions that depend on all live fragments together.
    /// </summary>
    public virtual void Prepare(LinkContext context, IEnumerable<Fragment> liveFragments)
    {
    }

    /// <summary>Decides which GOT entries, PLT stubs and dynamic relocations a relocation requires.</summary>
    public abstract void Scan(LinkContext context, Fragment fragment, in Relocation relocation, Symbol symbol);

    /// <summary>Patches <paramref name="data"/> (the content of <paramref name="fragment"/>) for one relocation.</summary>
    public abstract void Apply(LinkContext context, Fragment fragment, Span<byte> data, in Relocation relocation, Symbol symbol);

    /// <summary>
    /// Scans the relocation at <paramref name="index"/> and returns how many relocations were handled. Targets
    /// override this to recognize instruction sequences that span several relocations.
    /// </summary>
    public virtual int ScanAt(LinkContext context, Fragment fragment, int index, Symbol symbol)
    {
        Scan(context, fragment, fragment.Relocations[index], symbol);
        return 1;
    }

    /// <summary>Applies the relocation at <paramref name="index"/> and returns how many relocations were handled.</summary>
    public virtual int ApplyAt(LinkContext context, Fragment fragment, Span<byte> data, int index, Symbol symbol)
    {
        Apply(context, fragment, data, fragment.Relocations[index], symbol);
        return 1;
    }

    /// <summary>Writes a stub that jumps to the address stored in a GOT entry.</summary>
    public abstract void WritePltEntry(Span<byte> entry, ulong entryAddress, ulong gotEntryAddress);

    /// <summary>Creates the process entry point, which calls <c>__libc_start_main</c>.</summary>
    public abstract SyntheticFunction CreateEntryPoint(string entrySymbol, string mainSymbol);

    /// <summary>
    /// Creates the C library functions that shared C libraries leave to the static part of the toolchain.
    /// They are only linked in when something refers to them.
    /// </summary>
    public abstract IReadOnlyList<SyntheticFunction> CreateCLibraryStubs();

    /// <summary>Offset of a TLS address from the thread pointer.</summary>
    public abstract long ThreadPointerOffset(LinkContext context, ulong address);

    public abstract string RelocationName(uint type);

    public static TargetArch Create(TargetArchitecture architecture) => architecture switch
    {
        TargetArchitecture.X64 => new X64Target(),
        TargetArchitecture.Arm64 => new Arm64Target(),
        _ => throw new LinkerException($"unsupported target architecture: {architecture}"),
    };

    protected static LinkerException Unsupported(TargetArch target, Fragment fragment, in Relocation relocation, Symbol symbol) =>
        new($"{fragment.DisplayName}: unsupported relocation {target.RelocationName(relocation.Type)} against '{symbol.Name}'");

    protected static LinkerException OutOfRange(TargetArch target, Fragment fragment, in Relocation relocation, Symbol symbol) =>
        new($"{fragment.DisplayName}+0x{relocation.Offset:x}: relocation {target.RelocationName(relocation.Type)} against '{symbol.Name}' is out of range");
}
