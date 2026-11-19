using System.Diagnostics;
using System.Globalization;
using Ina.Linker;
using Ina.Linker.Abi;
using Microsoft.Build.Framework;

namespace Ina.Sdk;

/// <summary>
/// Links the object file that ILCompiler produced with the static libraries of the runtime into an executable.
/// The task takes the place of the native linker in the NativeAOT targets, so that <c>dotnet publish</c> needs
/// neither a native toolchain nor a sysroot of the target.
/// </summary>
public sealed class InaLink : Microsoft.Build.Utilities.Task
{
    /// <summary>Object files and static libraries, in link order.</summary>
    [Required]
    public ITaskItem[] Inputs { get; set; } = [];

    /// <summary>The executable to write.</summary>
    [Required]
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>
    /// The runtime identifier the inputs were compiled for. It selects the operating system, and with it the object
    /// format and the system libraries, and the architecture.
    /// </summary>
    [Required]
    public string RuntimeIdentifier { get; set; } = string.Empty;

    /// <summary>
    /// The file in which ILCompiler names the symbols that stay visible in the executable: a version script for
    /// GNU-style linkers, a list of symbols for Apple's, a module-definition file for Microsoft's.
    /// </summary>
    public string? ExportsFile { get; set; }

    /// <summary>
    /// Produce a position independent executable: for Windows one with base relocations, which the system loads at
    /// a random address. macOS has no other kind.
    /// </summary>
    public bool PositionIndependent { get; set; } = true;

    /// <summary>Keep a symbol table in the executable for debuggers and profilers. Windows executables have none.</summary>
    public bool EmitSymbolTable { get; set; }

    /// <summary>
    /// Windows only: the function where execution starts (<c>/ENTRY</c> of link.exe). By default the startup code
    /// that the linker generates, which calls <c>wmain</c>.
    /// </summary>
    public string? EntryPoint { get; set; }

    /// <summary>Windows only: <c>CONSOLE</c> or <c>WINDOWS</c> (<c>/SUBSYSTEM</c> of link.exe). By default a console application.</summary>
    public string? Subsystem { get; set; }

    /// <summary>
    /// Windows only: the address space reserved for the stack of the main thread, in bytes, decimal or hexadecimal
    /// with <c>0x</c> (<c>/STACK</c> of link.exe).
    /// </summary>
    public string? StackSize { get; set; }

    /// <summary>Windows only: sections to fold into others, as <c>from=to</c> (<c>/MERGE</c> of link.exe).</summary>
    public string[] MergedSections { get; set; } = [];

    public override bool Execute()
    {
        if (SupportedTargets.Find(RuntimeIdentifier) is not { } target)
        {
            LogError(
                Diagnostics.UnsupportedRuntimeIdentifier,
                $"Ina cannot produce executables for the runtime identifier '{RuntimeIdentifier}'. " +
                $"Supported runtime identifiers: {string.Join(", ", SupportedTargets.RuntimeIdentifiers)}.");
            return false;
        }

        if (ReadInputs() is not { } inputs || ReadExportedSymbols(target.System) is not { } exportedSymbols)
        {
            return false;
        }

        // The object format follows from the operating system. In every case the system libraries are an import
        // table that the linker brings along, never files of the target system.
        var clock = Stopwatch.StartNew();
        var logger = new TaskLogger(Log);
        LinkResult result;
        try
        {
            if (target.System == TargetSystem.Windows)
            {
                if (CreateWindowsOptions(target.Architecture, inputs, exportedSymbols) is not { } windowsOptions)
                {
                    return false;
                }

                result = PeLinker.Link(windowsOptions, logger);
            }
            else
            {
                var isMacOS = target.System == TargetSystem.MacOS;
                var options = new LinkOptions
                {
                    OutputPath = Path.GetFullPath(OutputPath),
                    Architecture = target.Architecture,
                    Imports = isMacOS ? ImportLibrarySet.ForMacOS(target.Architecture) : ImportLibrarySet.ForGlibc(target.Architecture),
                    Inputs = inputs,
                    PositionIndependent = PositionIndependent,
                    EmitSymbolTable = EmitSymbolTable,
                    ExportedSymbols = exportedSymbols,
                };
                result = isMacOS ? MachOLinker.Link(options, logger) : ElfLinker.Link(options, logger);
            }
        }
        catch (LinkerException e)
        {
            LogError(Diagnostics.LinkFailed, $"Linking {OutputPath} failed:{Environment.NewLine}{e.Message}");
            return false;
        }

        Log.LogMessage(
            MessageImportance.Low,
            "Linked {0} objects in {1:F0} ms: {2} imported symbols, {3} unused sections dropped, depends on {4}",
            result.ObjectCount, clock.Elapsed.TotalMilliseconds, result.ImportedSymbols.Count, result.DiscardedSectionCount,
            string.Join(", ", result.NeededLibraries));

        var minimumSystem = result.MinimumSystem is { } system ? $", {system}" : string.Empty;
        Log.LogMessage(
            MessageImportance.High,
            "Linked {0} with Ina ({1:F1} MB, {2}{3})",
            Path.GetFileName(OutputPath), result.FileSize / 1024.0 / 1024.0, RuntimeIdentifier, minimumSystem);
        return !Log.HasLoggedErrors;
    }

    private List<LinkInput>? ReadInputs()
    {
        var inputs = new List<LinkInput>(Inputs.Length);
        foreach (var item in Inputs)
        {
            var path = item.GetMetadata("FullPath");
            try
            {
                inputs.Add(LinkInput.FromFile(path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                LogError(Diagnostics.UnreadableInput, $"The link input {path} cannot be read: {e.Message}");
            }
        }

        return Log.HasLoggedErrors ? null : inputs;
    }

    /// <summary>
    /// The options of a Windows link, from what the NativeAOT targets pass to link.exe. Null when one of them is
    /// not understood, which has been reported.
    /// </summary>
    private PeLinkOptions? CreateWindowsOptions(TargetArchitecture architecture, List<LinkInput> inputs, IReadOnlyList<string> exportedSymbols)
    {
        var options = new PeLinkOptions
        {
            OutputPath = Path.GetFullPath(OutputPath),
            Architecture = architecture,
            Imports = ImportLibrarySet.ForWindows(),
            Inputs = inputs,
            DynamicBase = PositionIndependent,
            ExportedSymbols = exportedSymbols,
        };

        if (!string.IsNullOrEmpty(EntryPoint))
        {
            options = options with { EntryPoint = EntryPoint };
        }

        if (!string.IsNullOrEmpty(Subsystem))
        {
            // The version that link.exe accepts behind the name (CONSOLE,6.02) cannot be given to the linker.
            PeSubsystem? subsystem = Subsystem.ToUpperInvariant() switch
            {
                "CONSOLE" => PeSubsystem.Console,
                "WINDOWS" => PeSubsystem.Windows,
                _ => null,
            };
            if (subsystem is null)
            {
                LogError(Diagnostics.UnsupportedSetting, $"The subsystem '{Subsystem}' is not supported. Ina links console (CONSOLE) and graphical (WINDOWS) applications.");
                return null;
            }

            options = options with { Subsystem = subsystem.Value };
        }

        if (!string.IsNullOrEmpty(StackSize))
        {
            if (ParseSize(StackSize) is not { } stackSize)
            {
                LogError(Diagnostics.UnsupportedSetting, $"The stack size '{StackSize}' is not a number of bytes greater than zero.");
                return null;
            }

            options = options with { StackReserve = stackSize };
        }

        var mergedSections = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var merge in MergedSections)
        {
            var separator = merge.IndexOf('=');
            if (separator <= 0 || separator == merge.Length - 1)
            {
                LogError(Diagnostics.UnsupportedSetting, $"The merged section '{merge}' is not of the form from=to.");
                return null;
            }

            mergedSections[merge[..separator]] = merge[(separator + 1)..];
        }

        return options with { MergedSections = mergedSections };
    }

    /// <summary>A positive number of bytes, in decimal or C notation for hexadecimal, as link.exe takes it.</summary>
    private static ulong? ParseSize(string text)
    {
        var parsed = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var size)
            : ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out size);
        return parsed && size > 0 ? size : null;
    }

    private IReadOnlyList<string>? ReadExportedSymbols(TargetSystem system)
    {
        if (string.IsNullOrEmpty(ExportsFile))
        {
            return [];
        }

        try
        {
            var text = File.ReadAllText(ExportsFile);
            return system switch
            {
                TargetSystem.Windows => ModuleDefinitionFile.ReadExports(text),
                TargetSystem.MacOS => ExportedSymbolsList.Read(text),
                _ => VersionScript.ReadGlobalSymbols(text),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            LogError(Diagnostics.UnreadableInput, $"The exports file {ExportsFile} cannot be read: {e.Message}");
            return null;
        }
    }

    private void LogError(string code, string message) =>
        Log.LogError(subcategory: null, errorCode: code, helpKeyword: null, file: null, lineNumber: 0, columnNumber: 0, endLineNumber: 0, endColumnNumber: 0, message: message);
}
