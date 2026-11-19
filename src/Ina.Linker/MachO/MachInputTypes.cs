using Ina.Linker.Abi;

namespace Ina.Linker.MachO;

/// <summary>A raw symbol table entry (<c>nlist_64</c>) together with its name.</summary>
internal readonly record struct MachNlist(string Name, byte Type, byte SectionNumber, ushort Desc, ulong Value)
{
    /// <summary>Debugger symbols (stabs) share the table but are not symbols in the linker's sense.</summary>
    public bool IsStab => (Type & MachSymbolType.StabMask) != 0;

    public bool IsExternal => (Type & MachSymbolType.External) != 0;

    /// <summary>Visible to other object files but not exported from the image (hidden visibility).</summary>
    public bool IsPrivateExternal => (Type & MachSymbolType.PrivateExternal) != 0;

    private byte Kind => (byte)(Type & MachSymbolType.TypeMask);

    public bool IsDefinedInSection => Kind == MachSymbolType.Section;

    public bool IsAbsolute => Kind == MachSymbolType.Absolute;

    public bool IsIndirect => Kind == MachSymbolType.Indirect;

    /// <summary>A tentative definition: an undefined external symbol whose value is its size.</summary>
    public bool IsCommon => Kind == MachSymbolType.Undefined && IsExternal && Value != 0;

    public bool IsUndefined => Kind == MachSymbolType.Undefined && Value == 0;

    public bool IsWeakDefinition => (Desc & MachSymbolType.WeakDefinition) != 0 && !IsUndefined;

    public bool IsWeakReference => (Desc & MachSymbolType.WeakReference) != 0 && IsUndefined;

    public bool IsNoDeadStrip => (Desc & MachSymbolType.NoDeadStrip) != 0;

    /// <summary>An additional entry point into the atom of the preceding symbol; it does not start a new atom.</summary>
    public bool IsAltEntry => (Desc & MachSymbolType.AltEntry) != 0;

    /// <summary>The alignment a common symbol asks for, encoded as a power of two in <c>n_desc</c>.</summary>
    public ulong CommonAlignment => 1UL << ((Desc >> 8) & 0xf);
}

/// <summary>What a relocation computes, independent of the architecture specific relocation numbers.</summary>
internal enum MachRelocKind : byte
{
    /// <summary>An absolute address, or the difference of two addresses when a subtrahend is present.</summary>
    Pointer,

    /// <summary>A PC-relative call or jump (<c>BRANCH26</c>, <c>X86_64_RELOC_BRANCH</c>).</summary>
    Branch,

    /// <summary>AArch64 <c>adrp</c>: the page of the target.</summary>
    Page21,

    /// <summary>AArch64 <c>add</c>, load or store: the offset of the target inside its page.</summary>
    PageOff12,

    /// <summary>AArch64 <c>adrp</c> of a GOT entry; rewritten to address the symbol directly when it is local.</summary>
    GotLoadPage21,

    GotLoadPageOff12,

    /// <summary>AArch64 <c>adrp</c> of a thread-local variable descriptor.</summary>
    TlvLoadPage21,

    TlvLoadPageOff12,

    /// <summary>AArch64: the 32-bit distance from the place to a GOT entry.</summary>
    PointerToGot,

    /// <summary>x86-64: a 32-bit displacement relative to the end of the field (<c>SIGNED</c> and its variants).</summary>
    PcRel32,

    /// <summary>x86-64 <c>mov sym@GOTPCREL(%rip), reg</c>; rewritten to <c>lea</c> when the symbol is local.</summary>
    GotLoad,

    /// <summary>x86-64: any other RIP-relative reference to a GOT entry.</summary>
    Got,

    /// <summary>x86-64 <c>mov sym@TLVP(%rip), reg</c> for a thread-local variable descriptor.</summary>
    TlvLoad,
}

/// <summary>
/// A relocation after decoding. Mach-O spreads addends and differences over several entries and the section
/// content; here they are folded into one record: the value is <c>Symbol + Addend - Subtrahend</c>.
/// </summary>
/// <param name="Offset">Offset of the patched field inside the atom (or the section, for sections without atoms).</param>
/// <param name="Length">Size of the field as a power of two (2 = 4 bytes, 3 = 8 bytes).</param>
/// <param name="SymbolIndex">Index into <see cref="MachObjectFile.Symbols"/>.</param>
/// <param name="SubtrahendIndex">Index of the symbol that is subtracted, or -1.</param>
internal readonly record struct MachRelocation(uint Offset, MachRelocKind Kind, byte Length, int SymbolIndex, long Addend, int SubtrahendIndex = -1);

/// <summary>How the linker treats an input section.</summary>
internal enum MachSectionKind
{
    /// <summary>Split into atoms and copied to the output.</summary>
    Regular,

    /// <summary><c>__LD,__compact_unwind</c>: input for the generated <c>__unwind_info</c> table.</summary>
    CompactUnwind,

    /// <summary><c>__TEXT,__eh_frame</c>: handled record by record.</summary>
    EhFrame,

    /// <summary><c>__objc_imageinfo</c>: merged into a single record.</summary>
    ObjCImageInfo,

    /// <summary>Debug information and other content that is not loaded.</summary>
    Ignored,
}

internal enum MachSymbolKind
{
    /// <summary>Referenced but not (yet) defined.</summary>
    Undefined,

    /// <summary>Defined relative to an atom.</summary>
    Defined,

    /// <summary>A fixed value that does not move with the image.</summary>
    Absolute,

    /// <summary>A tentative definition that gets zero-filled storage unless a real definition shows up.</summary>
    Common,

    /// <summary>Provided at run time by a dynamic library.</summary>
    Imported,

    /// <summary>Defined by the linker itself; <see cref="MachSymbol.Value"/> holds the final address after layout.</summary>
    Synthetic,
}

/// <summary>A symbol after resolution. External symbols are shared between all files, local ones belong to a file.</summary>
internal sealed class MachSymbol(string name)
{
    public string Name { get; } = name;

    public MachSymbolKind Kind { get; set; }

    public MachObjectFile? File { get; set; }

    /// <summary>The defining atom when <see cref="Kind"/> is <see cref="MachSymbolKind.Defined"/>.</summary>
    public Atom? Atom { get; set; }

    /// <summary>Offset inside <see cref="Atom"/>, or the value of an absolute or synthetic symbol.</summary>
    public ulong Value { get; set; }

    public bool IsLocal { get; init; }

    public bool IsWeakDefinition { get; set; }

    public bool IsPrivateExternal { get; set; }

    /// <summary>Size of a common symbol.</summary>
    public ulong Size { get; set; }

    public ulong CommonAlignment { get; set; }

    /// <summary>Whether at least one non-weak reference exists. Symbols that are only referenced weakly may be missing at run time.</summary>
    public bool HasStrongReference { get; set; }

    /// <summary>Where the symbol comes from when <see cref="Kind"/> is <see cref="MachSymbolKind.Imported"/>.</summary>
    public ImportedSymbol? Import { get; set; }

    /// <summary>Whether the symbol must be visible to <c>dlsym</c> and other images.</summary>
    public bool IsExported { get; set; }

    /// <summary>For <c>section$start$SEG$sect</c> style symbols: the output section (or segment) they delimit.</summary>
    public MachBoundary? Boundary { get; set; }

    public int GotIndex { get; set; } = -1;

    public int StubIndex { get; set; } = -1;

    /// <summary>Index in the output symbol table, assigned while the table is written.</summary>
    public int OutputIndex { get; set; } = -1;

    public bool IsImported => Kind == MachSymbolKind.Imported;

    /// <summary>Whether the symbol has a link-time address that moves with the load address of the image.</summary>
    public bool IsImageRelative => Kind is MachSymbolKind.Defined or MachSymbolKind.Synthetic;

    /// <summary>Whether the symbol is a thread-local variable, i.e. names a descriptor in <c>__thread_vars</c>.</summary>
    public bool IsThreadLocal => Atom is { } atom && atom.Section.Type == MachSectionType.ThreadLocalVariables;

    /// <summary>The address of the symbol in the output image. Valid after layout.</summary>
    public ulong Address => Kind switch
    {
        MachSymbolKind.Defined => Atom is { } atom ? atom.Address + Value : 0,
        MachSymbolKind.Absolute or MachSymbolKind.Synthetic => Value,
        _ => 0,
    };

    public override string ToString() => Name;
}

/// <summary>The section or segment whose start or end a linker-defined symbol denotes.</summary>
internal sealed record MachBoundary(string Segment, string? Section, bool IsEnd);
