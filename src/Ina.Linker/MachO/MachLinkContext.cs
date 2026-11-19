namespace Ina.Linker.MachO;

/// <summary>A location at which the loader stores the address of an imported symbol.</summary>
internal readonly record struct MachBind(Atom Atom, uint Offset, MachSymbol Symbol, long Addend);

/// <summary>A location that holds an address of the image and has to be adjusted when the image is loaded elsewhere.</summary>
internal readonly record struct MachRebase(Atom Atom, uint Offset);

/// <summary>State shared between the linker passes, the targets and the generated sections.</summary>
internal sealed class MachLinkContext(LinkOptions options, MachTarget target)
{
    /// <summary>
    /// Where 64-bit executables are linked: directly above the 4 GiB that <c>__PAGEZERO</c> reserves so that
    /// truncated pointers fault.
    /// </summary>
    public const ulong DefaultImageBase = 0x1_0000_0000;

    public LinkOptions Options { get; } = options;

    public MachTarget Target { get; } = target;

    /// <summary>The link-time address of the Mach-O header.</summary>
    public ulong ImageBase => DefaultImageBase;

    /// <summary>Symbols with a GOT entry, in entry order.</summary>
    public List<MachSymbol> GotEntries { get; } = [];

    /// <summary>Imported functions that are called through a stub, in stub order.</summary>
    public List<MachSymbol> Stubs { get; } = [];

    public List<MachRebase> Rebases { get; } = [];

    public List<MachBind> Binds { get; } = [];

    public MachOutputSection? GotSection { get; set; }

    public MachOutputSection? StubsSection { get; set; }

    /// <summary>
    /// Address of the first thread-local data section. The loader copies everything from here to the end of the last
    /// one for every thread, and descriptors of thread-local variables hold offsets from this address.
    /// </summary>
    public ulong ThreadLocalTemplateStart { get; set; }

    public void AddGotEntry(MachSymbol symbol)
    {
        if (symbol.GotIndex < 0)
        {
            symbol.GotIndex = GotEntries.Count;
            GotEntries.Add(symbol);
        }
    }

    public void AddStub(MachSymbol symbol)
    {
        AddGotEntry(symbol);
        if (symbol.StubIndex < 0)
        {
            symbol.StubIndex = Stubs.Count;
            Stubs.Add(symbol);
        }
    }

    public void AddBind(Atom atom, uint offset, MachSymbol symbol, long addend)
    {
        RequireWritable(atom, symbol);
        Binds.Add(new MachBind(atom, offset, symbol, addend));
    }

    public void AddRebase(Atom atom, uint offset, MachSymbol symbol)
    {
        RequireWritable(atom, symbol);
        Rebases.Add(new MachRebase(atom, offset));
    }

    public ulong GotAddress(MachSymbol symbol) => GotSection!.Address + (ulong)symbol.GotIndex * 8;

    public ulong StubAddress(MachSymbol symbol) => StubsSection!.Address + (ulong)(symbol.StubIndex * Target.StubSize);

    private static void RequireWritable(Atom atom, MachSymbol symbol)
    {
        if (atom.Section.SegmentName == "__TEXT")
        {
            // The loader does not patch the read-only, signed text segment.
            throw new LinkerException(
                $"{atom.DisplayName}: absolute address of '{symbol.Name}' stored in a read-only section; the input must be position independent");
        }
    }
}
