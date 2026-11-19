using Ina.Linker.Abi;

namespace Ina.Linker;

/// <summary>The environment a Windows executable runs in.</summary>
public enum PeSubsystem
{
    /// <summary>A graphical application; no console window is created for it.</summary>
    Windows = 2,

    /// <summary>A console application.</summary>
    Console = 3,
}

/// <summary>Everything the linker needs to produce a Windows executable.</summary>
public sealed record PeLinkOptions
{
    /// <summary>Path of the executable to write.</summary>
    public required string OutputPath { get; init; }

    public required TargetArchitecture Architecture { get; init; }

    /// <summary>The DLLs whose exports may be imported, see <see cref="ImportLibrarySet.ForWindows"/>.</summary>
    public required ImportLibrarySet Imports { get; init; }

    /// <summary>Object files and static libraries, in command line order.</summary>
    public IReadOnlyList<LinkInput> Inputs { get; init; } = [];

    /// <summary>The function where execution starts. The default is the startup code that the linker generates.</summary>
    public string EntryPoint { get; init; } = "wmainCRTStartup";

    /// <summary>The <c>int wmain(int argc, wchar_t** argv)</c> that the generated startup code calls.</summary>
    public string MainSymbol { get; init; } = "wmain";

    public PeSubsystem Subsystem { get; init; } = PeSubsystem.Console;

    /// <summary>
    /// Produce an image that can be loaded at any address, which lets Windows randomize its location. Otherwise
    /// the image is bound to its preferred address and carries no base relocations.
    /// </summary>
    public bool DynamicBase { get; init; } = true;

    /// <summary>Drop COMDAT sections that are not reachable from the entry point.</summary>
    public bool GarbageCollectSections { get; init; } = true;

    /// <summary>Address space reserved for the stack of the main thread, in bytes. The default is that of NativeAOT.</summary>
    public ulong StackReserve { get; init; } = 1572864;

    /// <summary>Symbols that appear in the export table of the executable.</summary>
    public IReadOnlyList<string> ExportedSymbols { get; init; } = [];

    /// <summary>
    /// Sections that are folded into another section, by name (<c>/MERGE:from=to</c>). Objects can request more
    /// through their embedded linker options.
    /// </summary>
    public IReadOnlyDictionary<string, string> MergedSections { get; init; } = new Dictionary<string, string>();
}
