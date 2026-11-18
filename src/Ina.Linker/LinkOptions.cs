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

/// <summary>Everything the linker needs to produce an executable.</summary>
public sealed record LinkOptions
{
    /// <summary>Path of the executable to write.</summary>
    public required string OutputPath { get; init; }

    public required TargetArchitecture Architecture { get; init; }

    /// <summary>The shared libraries (C library etc.) whose symbols may be imported at run time.</summary>
    public required ImportLibrarySet Imports { get; init; }

    /// <summary>Object files and archives, in command line order.</summary>
    public IReadOnlyList<LinkInput> Inputs { get; init; } = [];

    /// <summary>The C-level entry point that the generated startup code hands to the C library.</summary>
    public string MainSymbol { get; init; } = "main";

    /// <summary>Produce a position independent executable (ET_DYN) rather than a fixed-address one.</summary>
    public bool PositionIndependent { get; init; } = true;

    /// <summary>Drop sections that are not reachable from the entry point.</summary>
    public bool GarbageCollectSections { get; init; } = true;

    /// <summary>Write a <c>.symtab</c> with the defined symbols, which helps debuggers and profilers.</summary>
    public bool EmitSymbolTable { get; init; }

    /// <summary>Symbols that must be visible in the dynamic symbol table of the executable.</summary>
    public IReadOnlyList<string> ExportedSymbols { get; init; } = [];
}

/// <summary>Statistics about a finished link.</summary>
public sealed record LinkResult(
    string OutputPath,
    long FileSize,
    int ObjectCount,
    int LiveSectionCount,
    int DiscardedSectionCount,
    IReadOnlyList<string> NeededLibraries,
    IReadOnlyList<string> ImportedSymbols,
    IReadOnlyList<string> RequiredVersions);
