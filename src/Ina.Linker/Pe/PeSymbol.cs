namespace Ina.Linker.Pe;

internal enum PeSymbolKind
{
    /// <summary>Referenced but not (yet) defined.</summary>
    Undefined,

    /// <summary>Defined at an offset inside a chunk.</summary>
    Defined,

    /// <summary>A fixed value that does not move with the image.</summary>
    Absolute,

    /// <summary>A tentative definition that is allocated in <c>.bss</c> unless a real definition shows up.</summary>
    Common,

    /// <summary>A function or variable of a DLL; turns into <see cref="Defined"/> once the import tables exist.</summary>
    Imported,
}

/// <summary>A function or variable that a DLL provides at run time.</summary>
internal sealed class PeImport(string library, string name)
{
    public string Library { get; } = library;

    /// <summary>The name the DLL exports the symbol under.</summary>
    public string Name { get; } = name;

    /// <summary>Whether a live section refers to the import, either through its address slot or through a thunk.</summary>
    public bool IsUsed { get; set; }

    /// <summary>Whether code calls the function directly, which goes through a generated thunk.</summary>
    public bool NeedsThunk { get; set; }

    /// <summary>The symbol for the slot of the import address table (<c>__imp_name</c>).</summary>
    public PeSymbol? AddressSymbol { get; set; }

    /// <summary>The symbol for the thunk that jumps through the slot (<c>name</c>).</summary>
    public PeSymbol? ThunkSymbol { get; set; }
}

/// <summary>A symbol after resolution. Global symbols are shared between all files, local ones belong to a file.</summary>
internal sealed class PeSymbol(string name)
{
    public string Name { get; } = name;

    public PeSymbolKind Kind { get; set; }

    public bool IsLocal { get; init; }

    /// <summary>The object that defines the symbol, for diagnostics.</summary>
    public CoffObject? File { get; set; }

    /// <summary>The defining chunk when <see cref="Kind"/> is <see cref="PeSymbolKind.Defined"/>.</summary>
    public PeChunk? Chunk { get; set; }

    /// <summary>The offset inside <see cref="Chunk"/>, or the value of an absolute symbol.</summary>
    public ulong Value { get; set; }

    /// <summary>The size requested by a common symbol.</summary>
    public uint CommonSize { get; set; }

    /// <summary>Whether a reference exists that requires a definition. Weak externals can do without one.</summary>
    public bool HasStrongReference { get; set; }

    /// <summary>The import behind the symbol when <see cref="Kind"/> is <see cref="PeSymbolKind.Imported"/>.</summary>
    public PeImport? Import { get; set; }

    /// <summary>
    /// The symbol that stands in when this one stays undefined: the default of a weak external, or the target of
    /// an <c>/alternatename</c> directive.
    /// </summary>
    public PeSymbol? Alias { get; set; }

    /// <summary>The symbol that references to this one actually bind to: itself, or the end of the alias chain.</summary>
    public PeSymbol Target
    {
        get
        {
            var symbol = this;

            // The bound guards against aliases that refer to each other.
            for (var depth = 0; depth < 16 && symbol is { Kind: PeSymbolKind.Undefined, Alias: { } alias }; depth++)
            {
                symbol = alias;
            }

            return symbol;
        }
    }

    /// <summary>The address relative to the image base. Valid after layout, for symbols defined in a live chunk.</summary>
    public uint Rva => Chunk is { } chunk ? chunk.Rva + (uint)Value : 0;

    public override string ToString() => Name;
}
