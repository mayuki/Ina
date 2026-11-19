namespace Ina.Linker.Pe;

/// <summary>Machine types of COFF objects and PE images (<c>IMAGE_FILE_MACHINE_*</c>).</summary>
internal static class CoffMachine
{
    public const ushort Unknown = 0;
    public const ushort Amd64 = 0x8664;
    public const ushort Arm64 = 0xaa64;
}

/// <summary>Section characteristics (<c>IMAGE_SCN_*</c>).</summary>
internal static class CoffSectionFlags
{
    public const uint Code = 0x0000_0020;
    public const uint InitializedData = 0x0000_0040;
    public const uint UninitializedData = 0x0000_0080;

    /// <summary>The section holds comments or linker directives (<c>.drectve</c>).</summary>
    public const uint LinkInfo = 0x0000_0200;

    /// <summary>The section does not become part of the image.</summary>
    public const uint LinkRemove = 0x0000_0800;

    public const uint LinkComdat = 0x0000_1000;
    public const uint AlignMask = 0x00f0_0000;

    /// <summary>The section has more than 0xffff relocations; the real count is in the first relocation.</summary>
    public const uint RelocationOverflow = 0x0100_0000;

    public const uint Discardable = 0x0200_0000;
    public const uint Execute = 0x2000_0000;
    public const uint Read = 0x4000_0000;
    public const uint Write = 0x8000_0000;

    /// <summary>The bits that describe the content and the protection of a section in an image.</summary>
    public const uint ImageMask = Code | InitializedData | UninitializedData | Discardable | Execute | Read | Write;
}

/// <summary>Storage classes of symbol table entries (<c>IMAGE_SYM_CLASS_*</c>).</summary>
internal static class CoffStorageClass
{
    public const byte External = 2;
    public const byte Static = 3;
    public const byte Label = 6;
    public const byte Function = 101;
    public const byte File = 103;
    public const byte Section = 104;
    public const byte WeakExternal = 105;
}

/// <summary>How the linker chooses between COMDAT sections with the same symbol (<c>IMAGE_COMDAT_SELECT_*</c>).</summary>
internal static class CoffComdat
{
    public const byte None = 0;
    public const byte NoDuplicates = 1;
    public const byte Any = 2;
    public const byte SameSize = 3;
    public const byte ExactMatch = 4;

    /// <summary>The section lives and dies with another section.</summary>
    public const byte Associative = 5;

    public const byte Largest = 6;
}

/// <summary>
/// How a weak external is resolved (<c>IMAGE_WEAK_EXTERN_SEARCH_*</c>). The linker treats all kinds alike: the
/// default stands in unless an object that is linked anyway defines the symbol.
/// </summary>
internal static class CoffWeakSearch
{
    public const uint NoLibrary = 1;
    public const uint Library = 2;
    public const uint Alias = 3;
}

/// <summary>Relocation types of x64 objects (<c>IMAGE_REL_AMD64_*</c>).</summary>
internal static class RelAmd64
{
    public const ushort Absolute = 0x0;
    public const ushort Addr64 = 0x1;
    public const ushort Addr32 = 0x2;

    /// <summary>The 32-bit address without the image base (an RVA).</summary>
    public const ushort Addr32Nb = 0x3;

    /// <summary>Relative to the byte following the relocated field; <c>Rel32_N</c> has N more bytes after the field.</summary>
    public const ushort Rel32 = 0x4;

    public const ushort Rel32_1 = 0x5;
    public const ushort Rel32_2 = 0x6;
    public const ushort Rel32_3 = 0x7;
    public const ushort Rel32_4 = 0x8;
    public const ushort Rel32_5 = 0x9;

    /// <summary>The index of the section that contains the target.</summary>
    public const ushort Section = 0xa;

    /// <summary>The offset of the target from the start of its section; used for thread-local variables.</summary>
    public const ushort SecRel = 0xb;
}

/// <summary>Relocation types of ARM64 objects (<c>IMAGE_REL_ARM64_*</c>).</summary>
internal static class RelArm64
{
    public const ushort Absolute = 0x0;
    public const ushort Addr32 = 0x1;
    public const ushort Addr32Nb = 0x2;
    public const ushort Branch26 = 0x3;
    public const ushort PageBaseRel21 = 0x4;
    public const ushort Rel21 = 0x5;
    public const ushort PageOffset12A = 0x6;
    public const ushort PageOffset12L = 0x7;
    public const ushort SecRel = 0x8;
    public const ushort SecRelLow12A = 0x9;
    public const ushort SecRelHigh12A = 0xa;
    public const ushort SecRelLow12L = 0xb;
    public const ushort Token = 0xc;
    public const ushort Section = 0xd;
    public const ushort Addr64 = 0xe;
    public const ushort Branch19 = 0xf;
    public const ushort Branch14 = 0x10;
    public const ushort Rel32 = 0x11;
}

/// <summary>Constants of the PE image format.</summary>
internal static class PeConstants
{
    public const int DosHeaderSize = 64;
    public const int FileHeaderSize = 20;
    public const int OptionalHeaderSize = 240;
    public const int SectionHeaderSize = 40;
    public const int DataDirectoryCount = 16;

    public const uint SectionAlignment = 0x1000;
    public const uint FileAlignment = 0x200;

    /// <summary>The preferred load address of 64-bit executables.</summary>
    public const ulong DefaultImageBase = 0x1_4000_0000;

    public const ushort Pe32PlusMagic = 0x20b;

    // IMAGE_FILE_*
    public const ushort FileRelocsStripped = 0x0001;
    public const ushort FileExecutableImage = 0x0002;
    public const ushort FileLargeAddressAware = 0x0020;

    // IMAGE_DLLCHARACTERISTICS_*
    public const ushort HighEntropyVa = 0x0020;
    public const ushort DynamicBase = 0x0040;
    public const ushort NxCompatible = 0x0100;
    public const ushort TerminalServerAware = 0x8000;

    // IMAGE_DIRECTORY_ENTRY_*
    public const int DirectoryExport = 0;
    public const int DirectoryImport = 1;
    public const int DirectoryResource = 2;
    public const int DirectoryException = 3;
    public const int DirectoryBaseRelocation = 5;
    public const int DirectoryTls = 9;
    public const int DirectoryLoadConfig = 10;
    public const int DirectoryImportAddressTable = 12;

    // IMAGE_REL_BASED_*
    public const int BaseRelocationAbsolute = 0;
    public const int BaseRelocationDir64 = 10;
}
