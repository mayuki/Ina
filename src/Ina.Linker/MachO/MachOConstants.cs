namespace Ina.Linker.MachO;

/// <summary>Values of the Mach-O header (<c>mach_header_64</c>).</summary>
internal static class MachHeader
{
    public const uint Magic64 = 0xfeedfacf;
    public const int Size = 32;

    public const uint FileTypeObject = 1;
    public const uint FileTypeExecute = 2;

    public const uint NoUndefs = 0x1;
    public const uint DyldLink = 0x4;
    public const uint TwoLevel = 0x80;
    public const uint SubsectionsViaSymbols = 0x2000;
    public const uint WeakDefines = 0x8000;
    public const uint BindsToWeak = 0x10000;
    public const uint Pie = 0x200000;
    public const uint HasTlvDescriptors = 0x800000;
}

/// <summary>Processor types (<c>cputype</c> and <c>cpusubtype</c>).</summary>
internal static class MachCpu
{
    public const uint X64 = 0x01000007;
    public const uint Arm64 = 0x0100000c;

    public const uint SubtypeX64All = 3;
    public const uint SubtypeArm64All = 0;
}

/// <summary>Load command identifiers (<c>cmd</c>).</summary>
internal static class LoadCommand
{
    public const uint RequiredByDyld = 0x80000000;

    public const uint SymTab = 0x2;
    public const uint DySymTab = 0xb;
    public const uint LoadDylib = 0xc;
    public const uint LoadDylinker = 0xe;
    public const uint LoadWeakDylib = 0x18 | RequiredByDyld;
    public const uint Segment64 = 0x19;
    public const uint Uuid = 0x1b;
    public const uint CodeSignature = 0x1d;
    public const uint DyldInfoOnly = 0x22 | RequiredByDyld;
    public const uint VersionMinMacOS = 0x24;
    public const uint FunctionStarts = 0x26;
    public const uint Main = 0x28 | RequiredByDyld;
    public const uint DataInCode = 0x29;
    public const uint SourceVersion = 0x2a;
    public const uint LinkerOption = 0x2d;
    public const uint BuildVersion = 0x32;

    public const int SegmentSize = 72;
    public const int SectionSize = 80;
}

/// <summary>Memory protection of a segment (<c>maxprot</c>, <c>initprot</c>) and segment flags.</summary>
internal static class SegmentProtection
{
    public const uint Read = 1;
    public const uint Write = 2;
    public const uint Execute = 4;

    /// <summary>The segment is made read-only after the loader applied its fixups (<c>SG_READ_ONLY</c>).</summary>
    public const uint FlagReadOnly = 0x10;
}

/// <summary>The type (low byte) and the attributes of a section (<c>flags</c>).</summary>
internal static class MachSectionType
{
    public const uint TypeMask = 0xff;

    public const uint Regular = 0x0;
    public const uint ZeroFill = 0x1;
    public const uint CStringLiterals = 0x2;
    public const uint Literals4 = 0x3;
    public const uint Literals8 = 0x4;
    public const uint LiteralPointers = 0x5;
    public const uint NonLazySymbolPointers = 0x6;
    public const uint LazySymbolPointers = 0x7;
    public const uint SymbolStubs = 0x8;
    public const uint ModInitFuncPointers = 0x9;
    public const uint ModTermFuncPointers = 0xa;
    public const uint Coalesced = 0xb;
    public const uint GigabyteZeroFill = 0xc;
    public const uint Literals16 = 0xe;
    public const uint ThreadLocalRegular = 0x11;
    public const uint ThreadLocalZeroFill = 0x12;
    public const uint ThreadLocalVariables = 0x13;
    public const uint ThreadLocalVariablePointers = 0x14;
    public const uint ThreadLocalInitFunctionPointers = 0x15;
    public const uint InitFuncOffsets = 0x16;

    public const uint AttrPureInstructions = 0x80000000;
    public const uint AttrNoToc = 0x40000000;
    public const uint AttrStripStaticSyms = 0x20000000;
    public const uint AttrNoDeadStrip = 0x10000000;
    public const uint AttrLiveSupport = 0x08000000;
    public const uint AttrDebug = 0x02000000;
    public const uint AttrSomeInstructions = 0x00000400;
}

/// <summary>Fields of a symbol table entry (<c>nlist_64</c>).</summary>
internal static class MachSymbolType
{
    public const int EntrySize = 16;

    // n_type
    public const byte StabMask = 0xe0;
    public const byte PrivateExternal = 0x10;
    public const byte TypeMask = 0x0e;
    public const byte External = 0x01;

    public const byte Undefined = 0x0;
    public const byte Absolute = 0x2;
    public const byte Indirect = 0xa;
    public const byte Section = 0xe;

    // n_desc
    public const ushort NoDeadStrip = 0x0020;
    public const ushort WeakReference = 0x0040;
    public const ushort WeakDefinition = 0x0080;
    public const ushort AltEntry = 0x0200;

    /// <summary>In a linked image, the ordinal of the library an undefined symbol is bound to.</summary>
    public static ushort LibraryOrdinal(int ordinal) => (ushort)((ordinal & 0xff) << 8);
}

/// <summary>Relocation types of AArch64 objects (<c>ARM64_RELOC_*</c>).</summary>
internal static class MachRelocArm64
{
    public const byte Unsigned = 0;
    public const byte Subtractor = 1;
    public const byte Branch26 = 2;
    public const byte Page21 = 3;
    public const byte PageOff12 = 4;
    public const byte GotLoadPage21 = 5;
    public const byte GotLoadPageOff12 = 6;
    public const byte PointerToGot = 7;
    public const byte TlvpLoadPage21 = 8;
    public const byte TlvpLoadPageOff12 = 9;
    public const byte Addend = 10;
}

/// <summary>Relocation types of x86-64 objects (<c>X86_64_RELOC_*</c>).</summary>
internal static class MachRelocX64
{
    public const byte Unsigned = 0;
    public const byte Signed = 1;
    public const byte Branch = 2;
    public const byte GotLoad = 3;
    public const byte Got = 4;
    public const byte Subtractor = 5;
    public const byte Signed1 = 6;
    public const byte Signed2 = 7;
    public const byte Signed4 = 8;
    public const byte Tlv = 9;
}

/// <summary>Opcodes of the rebase and bind programs that <c>LC_DYLD_INFO_ONLY</c> points to.</summary>
internal static class DyldInfo
{
    public const byte OpcodeMask = 0xf0;
    public const byte ImmediateMask = 0x0f;

    public const byte TypePointer = 1;

    public const byte RebaseDone = 0x00;
    public const byte RebaseSetTypeImm = 0x10;
    public const byte RebaseSetSegmentAndOffsetUleb = 0x20;
    public const byte RebaseAddAddrUleb = 0x30;
    public const byte RebaseAddAddrImmScaled = 0x40;
    public const byte RebaseDoRebaseImmTimes = 0x50;
    public const byte RebaseDoRebaseUlebTimes = 0x60;
    public const byte RebaseDoRebaseAddAddrUleb = 0x70;
    public const byte RebaseDoRebaseUlebTimesSkippingUleb = 0x80;

    public const byte BindDone = 0x00;
    public const byte BindSetDylibOrdinalImm = 0x10;
    public const byte BindSetDylibOrdinalUleb = 0x20;
    public const byte BindSetDylibSpecialImm = 0x30;
    public const byte BindSetSymbolTrailingFlagsImm = 0x40;
    public const byte BindSetTypeImm = 0x50;
    public const byte BindSetAddendSleb = 0x60;
    public const byte BindSetSegmentAndOffsetUleb = 0x70;
    public const byte BindAddAddrUleb = 0x80;
    public const byte BindDoBind = 0x90;
    public const byte BindDoBindAddAddrUleb = 0xa0;
    public const byte BindDoBindAddAddrImmScaled = 0xb0;
    public const byte BindDoBindUlebTimesSkippingUleb = 0xc0;

    public const byte BindSymbolFlagsWeakImport = 0x1;

    public const uint IndirectSymbolLocal = 0x80000000;
}

/// <summary>Compact unwind encodings (<c>compact_unwind_encoding.h</c>), as far as the linker interprets them.</summary>
internal static class CompactUnwind
{
    /// <summary>Size of an entry of <c>__LD,__compact_unwind</c> in a 64-bit object.</summary>
    public const int EntrySize = 32;

    public const uint HasLsda = 0x40000000;
    public const uint PersonalityMask = 0x30000000;
    public const int PersonalityShift = 28;
    public const uint ModeMask = 0x0f000000;

    /// <summary>The unwinder has to use the FDE whose offset in <c>__eh_frame</c> is in the low 24 bits.</summary>
    public const uint ModeDwarfArm64 = 0x03000000;
    public const uint ModeDwarfX64 = 0x04000000;
    public const uint DwarfSectionOffsetMask = 0x00ffffff;

    public const uint SecondLevelRegular = 2;
    public const uint SecondLevelCompressed = 3;
}
