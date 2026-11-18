namespace Ina.Linker.Elf;

/// <summary>ELF machine identifiers (e_machine).</summary>
internal enum ElfMachine : ushort
{
    X86_64 = 62,
    AArch64 = 183,
}

/// <summary>ELF file types (e_type).</summary>
internal static class ElfType
{
    public const ushort Rel = 1;
    public const ushort Exec = 2;
    public const ushort Dyn = 3;
}

/// <summary>Section types (sh_type).</summary>
internal static class SectionType
{
    public const uint Null = 0;
    public const uint ProgBits = 1;
    public const uint SymTab = 2;
    public const uint StrTab = 3;
    public const uint Rela = 4;
    public const uint Hash = 5;
    public const uint Dynamic = 6;
    public const uint Note = 7;
    public const uint NoBits = 8;
    public const uint Rel = 9;
    public const uint DynSym = 11;
    public const uint InitArray = 14;
    public const uint FiniArray = 15;
    public const uint PreInitArray = 16;
    public const uint Group = 17;
    public const uint SymTabShndx = 18;
    public const uint GnuVerDef = 0x6ffffffd;
    public const uint GnuVerNeed = 0x6ffffffe;
    public const uint GnuVerSym = 0x6fffffff;
    public const uint X86_64Unwind = 0x70000001;
}

/// <summary>Section flags (sh_flags).</summary>
internal static class SectionFlags
{
    public const ulong Write = 0x1;
    public const ulong Alloc = 0x2;
    public const ulong ExecInstr = 0x4;
    public const ulong Merge = 0x10;
    public const ulong Strings = 0x20;
    public const ulong InfoLink = 0x40;
    public const ulong LinkOrder = 0x80;
    public const ulong Group = 0x200;
    public const ulong Tls = 0x400;
    public const ulong Compressed = 0x800;
    public const ulong GnuRetain = 0x200000;
    public const ulong Exclude = 0x80000000;
}

/// <summary>Special section indices (st_shndx).</summary>
internal static class SectionIndex
{
    public const ushort Undefined = 0;
    public const ushort LoReserve = 0xff00;
    public const ushort Abs = 0xfff1;
    public const ushort Common = 0xfff2;
    public const ushort XIndex = 0xffff;
}

internal static class SymbolBinding
{
    public const byte Local = 0;
    public const byte Global = 1;
    public const byte Weak = 2;
    public const byte GnuUnique = 10;
}

internal static class SymbolType
{
    public const byte NoType = 0;
    public const byte Object = 1;
    public const byte Func = 2;
    public const byte Section = 3;
    public const byte File = 4;
    public const byte Common = 5;
    public const byte Tls = 6;
    public const byte GnuIFunc = 10;
}

internal static class SymbolVisibility
{
    public const byte Default = 0;
    public const byte Internal = 1;
    public const byte Hidden = 2;
    public const byte Protected = 3;
}

/// <summary>Program header types (p_type).</summary>
internal static class SegmentType
{
    public const uint Load = 1;
    public const uint Dynamic = 2;
    public const uint Interp = 3;
    public const uint Note = 4;
    public const uint Phdr = 6;
    public const uint Tls = 7;
    public const uint GnuEhFrame = 0x6474e550;
    public const uint GnuStack = 0x6474e551;
    public const uint GnuRelRo = 0x6474e552;
}

internal static class SegmentFlags
{
    public const uint Execute = 1;
    public const uint Write = 2;
    public const uint Read = 4;
}

/// <summary>Dynamic section tags (d_tag).</summary>
internal static class DynamicTag
{
    public const long Null = 0;
    public const long Needed = 1;
    public const long Hash = 4;
    public const long StrTab = 5;
    public const long SymTab = 6;
    public const long Rela = 7;
    public const long RelaSz = 8;
    public const long RelaEnt = 9;
    public const long StrSz = 10;
    public const long SymEnt = 11;
    public const long Debug = 21;
    public const long InitArray = 25;
    public const long FiniArray = 26;
    public const long InitArraySz = 27;
    public const long FiniArraySz = 28;
    public const long Flags = 30;
    public const long PreInitArray = 32;
    public const long PreInitArraySz = 33;
    public const long RelaCount = 0x6ffffff9;
    public const long Flags1 = 0x6ffffffb;
    public const long VerSym = 0x6ffffff0;
    public const long VerNeed = 0x6ffffffe;
    public const long VerNeedNum = 0x6fffffff;

    public const ulong FlagBindNow = 0x8;
    public const ulong Flag1Now = 0x1;
    public const ulong Flag1Pie = 0x08000000;
}

/// <summary>x86-64 relocation types.</summary>
internal static class RelocX64
{
    public const uint None = 0;
    public const uint Abs64 = 1;
    public const uint Pc32 = 2;
    public const uint Got32 = 3;
    public const uint Plt32 = 4;
    public const uint Copy = 5;
    public const uint GlobDat = 6;
    public const uint JumpSlot = 7;
    public const uint Relative = 8;
    public const uint GotPcRel = 9;
    public const uint Abs32 = 10;
    public const uint Abs32S = 11;
    public const uint Abs16 = 12;
    public const uint Pc16 = 13;
    public const uint Abs8 = 14;
    public const uint Pc8 = 15;
    public const uint DtpMod64 = 16;
    public const uint DtpOff64 = 17;
    public const uint TpOff64 = 18;
    public const uint TlsGd = 19;
    public const uint TlsLd = 20;
    public const uint DtpOff32 = 21;
    public const uint GotTpOff = 22;
    public const uint TpOff32 = 23;
    public const uint Pc64 = 24;
    public const uint GotOff64 = 25;
    public const uint GotPc32 = 26;
    public const uint Size32 = 32;
    public const uint Size64 = 33;
    public const uint GotPcRelX = 41;
    public const uint RexGotPcRelX = 42;
}

/// <summary>AArch64 relocation types.</summary>
internal static class RelocArm64
{
    public const uint None = 0;
    public const uint Abs64 = 257;
    public const uint Abs32 = 258;
    public const uint Abs16 = 259;
    public const uint Prel64 = 260;
    public const uint Prel32 = 261;
    public const uint Prel16 = 262;
    public const uint MovwUabsG0 = 263;
    public const uint MovwUabsG0Nc = 264;
    public const uint MovwUabsG1 = 265;
    public const uint MovwUabsG1Nc = 266;
    public const uint MovwUabsG2 = 267;
    public const uint MovwUabsG2Nc = 268;
    public const uint MovwUabsG3 = 269;
    public const uint LdPrelLo19 = 273;
    public const uint AdrPrelLo21 = 274;
    public const uint AdrPrelPgHi21 = 275;
    public const uint AdrPrelPgHi21Nc = 276;
    public const uint AddAbsLo12Nc = 277;
    public const uint Ldst8AbsLo12Nc = 278;
    public const uint TstBr14 = 279;
    public const uint CondBr19 = 280;
    public const uint Jump26 = 282;
    public const uint Call26 = 283;
    public const uint Ldst16AbsLo12Nc = 284;
    public const uint Ldst32AbsLo12Nc = 285;
    public const uint Ldst64AbsLo12Nc = 286;
    public const uint Ldst128AbsLo12Nc = 299;
    public const uint AdrGotPage = 311;
    public const uint Ld64GotLo12Nc = 312;
    public const uint TlsGdAdrPage21 = 513;
    public const uint TlsGdAddLo12Nc = 514;
    public const uint TlsIeAdrGotTprelPage21 = 541;
    public const uint TlsIeLd64GotTprelLo12Nc = 542;
    public const uint TlsLeAddTprelHi12 = 549;
    public const uint TlsLeAddTprelLo12 = 550;
    public const uint TlsLeAddTprelLo12Nc = 551;
    public const uint TlsDescAdrPage21 = 562;
    public const uint TlsDescLd64Lo12 = 563;
    public const uint TlsDescAddLo12 = 564;
    public const uint TlsDescCall = 569;
    public const uint Copy = 1024;
    public const uint GlobDat = 1025;
    public const uint JumpSlot = 1026;
    public const uint Relative = 1027;
    public const uint TlsDtpMod64 = 1028;
    public const uint TlsDtpRel64 = 1029;
    public const uint TlsTpRel64 = 1030;
    public const uint TlsDesc = 1031;
}
