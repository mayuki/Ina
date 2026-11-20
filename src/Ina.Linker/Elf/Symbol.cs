using Ina.Linker.Abi;

namespace Ina.Linker.Elf;

internal enum SymbolKind
{
    /// <summary>Referenced but not (yet) defined.</summary>
    Undefined,

    /// <summary>Defined relative to an input section.</summary>
    Defined,

    /// <summary>A fixed value that does not move with the image.</summary>
    Absolute,

    /// <summary>A tentative definition that is allocated in <c>.bss</c> unless a real definition shows up.</summary>
    Common,

    /// <summary>Provided at run time by a shared library.</summary>
    Imported,

    /// <summary>Defined by the linker itself; <see cref="Symbol.Value"/> holds the final address after layout.</summary>
    Synthetic,
}

/// <summary>A symbol after resolution. Global symbols are shared between all files, local ones belong to a file.</summary>
internal sealed class Symbol(string name)
{
    public string Name { get; } = name;

    public SymbolKind Kind { get; set; }

    public ElfObjectFile? File { get; set; }

    /// <summary>The defining section when <see cref="Kind"/> is <see cref="SymbolKind.Defined"/>.</summary>
    public InputSection? Section { get; set; }

    public ulong Value { get; set; }

    public ulong Size { get; set; }

    public byte Type { get; set; }

    public byte Binding { get; set; }

    public byte Visibility { get; set; }

    public bool IsLocal { get; init; }

    /// <summary>Alignment requested by a common symbol.</summary>
    public ulong CommonAlignment { get; set; }

    /// <summary>Whether at least one non-weak reference exists. Weak-only references may stay unresolved.</summary>
    public bool HasStrongReference { get; set; }

    /// <summary>For <c>__start_X</c> / <c>__stop_X</c> symbols, the section name X they delimit.</summary>
    public string? StartStopSection { get; set; }

    /// <summary>Where the symbol comes from when <see cref="Kind"/> is <see cref="SymbolKind.Imported"/>.</summary>
    public ImportedSymbol? Import { get; set; }

    /// <summary>Whether the symbol must appear in the dynamic symbol table as a definition.</summary>
    public bool IsExported { get; set; }

    public int GotIndex { get; set; } = -1;

    public int PltIndex { get; set; } = -1;

    public int TlsGdIndex { get; set; } = -1;

    public int GotTpIndex { get; set; } = -1;

    public int DynamicIndex { get; set; } = -1;

    public bool IsTls => Type == SymbolType.Tls;

    public bool IsWeak => Binding == SymbolBinding.Weak;

    public bool IsImported => Kind == SymbolKind.Imported;

    public bool IsDefined => Kind is SymbolKind.Defined or SymbolKind.Absolute or SymbolKind.Common or SymbolKind.Synthetic;

    /// <summary>Whether the symbol has a link-time address that moves with the load address of the image.</summary>
    public bool IsImageRelative => Kind is SymbolKind.Defined or SymbolKind.Synthetic;

    /// <summary>The address of the symbol in the output image. Valid after layout.</summary>
    public ulong Address => Kind switch
    {
        SymbolKind.Defined => Section is { IsLive: true } section ? section.Address + Value : 0,
        SymbolKind.Absolute or SymbolKind.Synthetic => Value,
        _ => 0,
    };

    public override string ToString() => Name;
}
