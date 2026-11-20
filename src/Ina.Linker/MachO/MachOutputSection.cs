
namespace Ina.Linker.MachO;

/// <summary>A segment of the output image: a run of sections with the same memory protection.</summary>
internal sealed class MachOutputSegment(string name, uint protection, uint flags = 0)
{
    public string Name { get; } = name;

    public uint Protection { get; } = protection;

    public uint Flags { get; } = flags;

    public List<MachOutputSection> Sections { get; } = [];

    public ulong Address { get; set; }

    public ulong VmSize { get; set; }

    public ulong FileOffset { get; set; }

    public ulong FileSize { get; set; }

    /// <summary>Position among the segment load commands, which is how the loader's fixup programs name segments.</summary>
    public int Index { get; set; }
}

/// <summary>A section of the output image. The base class holds atoms of input sections with the same name.</summary>
internal class MachOutputSection(string segmentName, string sectionName, uint flags)
{
    public string SegmentName { get; } = segmentName;

    public string SectionName { get; } = sectionName;

    public uint Flags { get; } = flags;

    public ulong Alignment { get; protected set; } = 1;

    public ulong Address { get; set; }

    public ulong Size { get; protected set; }

    public ulong FileOffset { get; set; }

    /// <summary>For stubs and symbol pointers: the index of the first entry in the indirect symbol table.</summary>
    public uint Reserved1 { get; set; }

    /// <summary>For stubs: the size of a stub.</summary>
    public uint Reserved2 { get; protected set; }

    /// <summary>The one-based section number in the output image, as used by the symbol table.</summary>
    public int Index { get; set; }

    public List<Atom> Atoms { get; } = [];

    public uint Type => Flags & MachSectionType.TypeMask;

    public bool IsZeroFill => Type is MachSectionType.ZeroFill or MachSectionType.GigabyteZeroFill or MachSectionType.ThreadLocalZeroFill;

    public bool IsCode => (Flags & MachSectionType.AttrPureInstructions) != 0 || (SegmentName == "__TEXT" && SectionName == "__text");

    public void Add(Atom atom)
    {
        Atoms.Add(atom);
        atom.Parent = this;
        Alignment = Math.Max(Alignment, atom.Alignment);
    }

    /// <summary>
    /// Determines the size of the section. Called in address order during layout, so the addresses of all sections
    /// in front of this one are final.
    /// </summary>
    public virtual void Finish(MachLinkContext context)
    {
        var offset = 0UL;
        foreach (var atom in Atoms)
        {
            offset = (offset + atom.Alignment - 1) & ~(atom.Alignment - 1);
            atom.Offset = offset;
            offset += atom.Size;
        }

        Size = offset;
    }

    /// <summary>Writes the content of the section; <paramref name="data"/> is its zero-initialized place in the file.</summary>
    public virtual void Write(MachLinkContext context, Span<byte> data)
    {
        foreach (var atom in Atoms)
        {
            var target = data.Slice((int)atom.Offset, (int)atom.Size);
            atom.Content.CopyTo(target);
            foreach (ref readonly var relocation in atom.Relocations.AsSpan())
            {
                context.Target.Apply(context, atom, target, relocation);
            }
        }
    }

    public override string ToString() => $"{SegmentName},{SectionName}";
}

/// <summary>
/// The global offset table: one pointer per symbol whose address code loads from memory. Entries of imported
/// symbols are filled in by the loader, the others hold addresses of the image.
/// </summary>
internal sealed class MachGotSection : MachOutputSection
{
    public MachGotSection()
        : base("__DATA_CONST", "__got", MachSectionType.NonLazySymbolPointers)
    {
        Alignment = 8;
    }

    public override void Finish(MachLinkContext context) => Size = (ulong)context.GotEntries.Count * 8;

    public override void Write(MachLinkContext context, Span<byte> data)
    {
        for (var i = 0; i < context.GotEntries.Count; i++)
        {
            LittleEndian.U64(data, i * 8, context.GotEntries[i].Address);
        }
    }
}

/// <summary>Stubs through which imported functions are called: each one jumps to the address in a GOT entry.</summary>
internal sealed class MachStubsSection : MachOutputSection
{
    public MachStubsSection(MachTarget target)
        : base("__TEXT", "__stubs", MachSectionType.SymbolStubs | MachSectionType.AttrPureInstructions | MachSectionType.AttrSomeInstructions)
    {
        Alignment = target.StubAlignment;
        Reserved2 = (uint)target.StubSize;
    }

    public override void Finish(MachLinkContext context) => Size = (ulong)(context.Stubs.Count * context.Target.StubSize);

    public override void Write(MachLinkContext context, Span<byte> data)
    {
        var size = context.Target.StubSize;
        for (var i = 0; i < context.Stubs.Count; i++)
        {
            var symbol = context.Stubs[i];
            context.Target.WriteStub(data.Slice(i * size, size), context.StubAddress(symbol), context.GotAddress(symbol));
        }
    }
}

/// <summary>
/// The single <c>__objc_imageinfo</c> record of the image, merged from the records of the object files that
/// contain Objective-C or Swift code.
/// </summary>
internal sealed class MachImageInfoSection : MachOutputSection
{
    private const uint HasCategoryClassProperties = 0x40;
    private const uint SwiftVersionMask = 0xff00;

    private uint _flags;
    private bool _hasRecord;

    public MachImageInfoSection()
        : base("__DATA_CONST", "__objc_imageinfo", MachSectionType.Regular | MachSectionType.AttrNoDeadStrip)
    {
        Alignment = 4;
        Size = 8;
    }

    /// <summary>Folds the record of one object file into the result.</summary>
    public void Merge(MachInputSection section)
    {
        if (section.Content.Length < 8)
        {
            return;
        }

        var flags = LittleEndian.U32(section.Content.Span, 4);
        if (!_hasRecord)
        {
            _flags = flags;
            _hasRecord = true;
            return;
        }

        // A property holds for the image only if all objects have it; the Swift version is that of the Swift code.
        var swift = Math.Max(_flags & SwiftVersionMask, flags & SwiftVersionMask);
        var common = _flags & flags & HasCategoryClassProperties;
        _flags = (_flags & ~(SwiftVersionMask | HasCategoryClassProperties)) | swift | common;
    }

    public override void Finish(MachLinkContext context)
    {
    }

    public override void Write(MachLinkContext context, Span<byte> data) => LittleEndian.U32(data, 4, _flags);
}

/// <summary>Where input sections go in the output image and in which order output sections are laid out.</summary>
internal static class MachSectionLayout
{
    public const string TextSegment = "__TEXT";
    public const string DataConstSegment = "__DATA_CONST";
    public const string DataSegment = "__DATA";
    public const string LinkEditSegment = "__LINKEDIT";

    /// <summary>
    /// Sections of <c>__DATA</c> that are not modified after the loader has applied its fixups. They are moved to
    /// <c>__DATA_CONST</c>, which the loader makes read-only afterwards.
    /// </summary>
    private static readonly HashSet<string> ConstantDataSections = new(StringComparer.Ordinal)
    {
        "__const", "__cfstring", "__mod_init_func", "__mod_term_func", "__objc_classlist", "__objc_nlclslist", "__objc_catlist",
        "__objc_nlcatlist", "__objc_protolist",
    };

    /// <summary>Returns the segment and section that an input section is merged into.</summary>
    public static (string Segment, string Section) OutputName(string segment, string section) => (segment, section) switch
    {
        (DataSegment, _) when ConstantDataSections.Contains(section) => (DataConstSegment, section),
        (TextSegment, "__textcoal_nt" or "__StaticInit") => (TextSegment, "__text"),
        (TextSegment, "__const_coal") => (TextSegment, "__const"),
        (DataSegment, "__datacoal_nt") => (DataSegment, "__data"),
        _ => (segment, section),
    };

    /// <summary>
    /// The position of a section inside its segment; sections of equal rank keep the order in which they were
    /// first seen. Code comes first, the unwind tables end the text segment, and zero-filled sections end a data
    /// segment because they have no content in the file.
    /// </summary>
    public static int Rank(MachOutputSection section)
    {
        if (section.SegmentName == TextSegment)
        {
            return section.SectionName switch
            {
                "__text" => 0,
                "__stubs" => 2,
                "__unwind_info" => 8,
                "__eh_frame" => 9,
                _ => section.IsCode ? 1 : 5,
            };
        }

        if (section.SectionName == "__got")
        {
            return 0;
        }

        // The thread-local sections form the template of the per-thread data and must be adjacent.
        return section.Type switch
        {
            MachSectionType.ThreadLocalVariables => 5,
            MachSectionType.ThreadLocalRegular => 6,
            MachSectionType.ThreadLocalZeroFill => 7,
            MachSectionType.ZeroFill or MachSectionType.GigabyteZeroFill => 8,
            _ => 2,
        };
    }
}
