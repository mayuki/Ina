using System.Diagnostics;
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
    /// GNU-style linkers, a list of symbols for Apple's.
    /// </summary>
    public string? ExportsFile { get; set; }

    /// <summary>Produce a position independent executable. macOS has no other kind.</summary>
    public bool PositionIndependent { get; set; } = true;

    /// <summary>Keep a symbol table in the executable for debuggers and profilers.</summary>
    public bool EmitSymbolTable { get; set; }

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

        var isMacOS = target.System == TargetSystem.MacOS;
        if (ReadInputs() is not { } inputs || ReadExportedSymbols(isMacOS) is not { } exportedSymbols)
        {
            return false;
        }

        var clock = Stopwatch.StartNew();
        LinkResult result;
        try
        {
            // The object format follows from the operating system. Either way the system libraries are an import
            // table that the linker brings along, never files of the target system.
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
            var logger = new TaskLogger(Log);
            result = isMacOS ? MachOLinker.Link(options, logger) : ElfLinker.Link(options, logger);
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

    private IReadOnlyList<string>? ReadExportedSymbols(bool isMacOS)
    {
        if (string.IsNullOrEmpty(ExportsFile))
        {
            return [];
        }

        try
        {
            var text = File.ReadAllText(ExportsFile);
            return isMacOS ? ExportedSymbolsList.Read(text) : VersionScript.ReadGlobalSymbols(text);
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
