using Ina.Linker.Abi;

namespace Ina.Linker;

/// <summary>The processor architecture of the linked image.</summary>
public enum TargetArchitecture
{
    X64,
    Arm64,
}

/// <summary>An object file or static library handed to the linker.</summary>
public sealed record LinkInput(string Name, ReadOnlyMemory<byte> Data)
{
    public static LinkInput FromFile(string path) => new(path, File.ReadAllBytes(path));
}

/// <summary>
/// Everything a linker needs to produce an executable. The options are shared by <see cref="ElfLinker"/> and
/// <see cref="MachOLinker"/>; symbol names are given the way C code spells them.
/// </summary>
public sealed record LinkOptions
{
    /// <summary>Path of the executable to write.</summary>
    public required string OutputPath { get; init; }

    public required TargetArchitecture Architecture { get; init; }

    /// <summary>The system libraries (C library etc.) whose symbols may be imported at run time.</summary>
    public required ImportLibrarySet Imports { get; init; }

    /// <summary>Object files and archives, in command line order.</summary>
    public IReadOnlyList<LinkInput> Inputs { get; init; } = [];

    /// <summary>
    /// The C-level entry point. For ELF the generated startup code hands it to the C library; for Mach-O the loader
    /// calls it directly.
    /// </summary>
    public string MainSymbol { get; init; } = "main";

    /// <summary>
    /// Produce a position independent executable (ET_DYN) rather than a fixed-address one. macOS executables are
    /// always position independent.
    /// </summary>
    public bool PositionIndependent { get; init; } = true;

    /// <summary>Drop sections (ELF) or atoms (Mach-O) that are not reachable from the entry point.</summary>
    public bool GarbageCollectSections { get; init; } = true;

    /// <summary>Write a symbol table with the defined symbols, which helps debuggers and profilers.</summary>
    public bool EmitSymbolTable { get; init; }

    /// <summary>Symbols that other images and <c>dlsym</c> must be able to find in the executable.</summary>
    public IReadOnlyList<string> ExportedSymbols { get; init; } = [];
}

/// <summary>Statistics about a finished link.</summary>
/// <param name="LiveSectionCount">The number of input sections (ELF) or atoms (Mach-O) that are part of the output.</param>
/// <param name="DiscardedSectionCount">The number of input sections or atoms that were dropped as unreachable.</param>
/// <param name="NeededLibraries">The system libraries the executable depends on, as the loader looks them up.</param>
/// <param name="RequiredVersions">
/// What the executable requires of the system: versioned symbol sets such as <c>libc.so.6:GLIBC_2.17</c> for ELF,
/// the minimum operating system such as <c>macOS 12.0</c> for Mach-O.
/// </param>
public sealed record LinkResult(
    string OutputPath,
    long FileSize,
    int ObjectCount,
    int LiveSectionCount,
    int DiscardedSectionCount,
    IReadOnlyList<string> NeededLibraries,
    IReadOnlyList<string> ImportedSymbols,
    IReadOnlyList<string> RequiredVersions);
