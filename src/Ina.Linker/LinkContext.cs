using Ina.Linker.Input;
using Ina.Linker.Output;
using Ina.Linker.Symbols;
using Ina.Linker.Targets;

namespace Ina.Linker;

/// <summary>State shared between the linker passes and the output chunks.</summary>
internal sealed class LinkContext
{
    public LinkContext(LinkOptions options, TargetArch target)
    {
        Options = options;
        Target = target;
        IsPositionIndependent = options.PositionIndependent;
        ImageBase = options.PositionIndependent ? 0 : target.DefaultImageBase;
    }

    public LinkOptions Options { get; }

    public TargetArch Target { get; }

    public bool IsPositionIndependent { get; }

    /// <summary>The link-time address of the ELF header.</summary>
    public ulong ImageBase { get; }

    public GotSection Got { get; } = new();

    public PltSection Plt { get; } = new();

    public List<DynamicRelocation> DynamicRelocations { get; } = [];

    /// <summary>Address of the start of the TLS initialization image.</summary>
    public ulong TlsStart { get; set; }

    /// <summary>Size of the TLS block in memory, including zero-initialized variables.</summary>
    public ulong TlsSize { get; set; }

    public ulong TlsAlignment { get; set; } = 1;

    /// <summary>Records a relocation that makes the loader store the address of an imported symbol.</summary>
    public void AddSymbolicRelocation(Fragment fragment, ulong offset, Symbol symbol, long addend)
    {
        RequireWritable(fragment, symbol);
        DynamicRelocations.Add(new DynamicRelocation
        {
            Fragment = fragment, Offset = offset, Type = Target.Abs64Relocation, DynamicSymbol = symbol, Addend = addend,
        });
    }

    /// <summary>Records a relocation that makes the loader adjust a stored address by the load address.</summary>
    public void AddRelativeRelocation(Fragment fragment, ulong offset, Symbol symbol, long addend)
    {
        RequireWritable(fragment, symbol);
        DynamicRelocations.Add(new DynamicRelocation
        {
            Fragment = fragment, Offset = offset, Type = Target.RelativeRelocation, Target = symbol, Addend = addend,
        });
    }

    /// <summary>Applies all relocations of <paramref name="fragment"/> to its copy in the output buffer.</summary>
    public void ApplyRelocations(Fragment fragment, Span<byte> data)
    {
        var symbols = fragment.File.Symbols;
        foreach (ref readonly var relocation in fragment.Relocations.AsSpan())
        {
            Target.Apply(this, fragment, data, relocation, symbols[relocation.SymbolIndex]);
        }
    }

    private static void RequireWritable(Fragment fragment, Symbol symbol)
    {
        if (!fragment.IsWritable)
        {
            // Patching read-only memory at load time would need text relocations, which Ina does not generate.
            throw new LinkerException(
                $"{fragment.DisplayName}: absolute address of '{symbol.Name}' stored in a read-only section; the input must be position independent");
        }
    }
}
