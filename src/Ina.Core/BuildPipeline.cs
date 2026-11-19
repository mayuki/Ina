using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Ina.Compilation;
using Ina.Diagnostics;
using Ina.Linker;
using Ina.Linker.Abi;
using Ina.Packs;
using Microsoft.Extensions.Logging;

namespace Ina;

/// <summary>Everything that can be chosen for a build.</summary>
public sealed record BuildOptions
{
    /// <summary>The application: a .NET assembly with an entry point.</summary>
    public required string AssemblyPath { get; init; }

    /// <summary>
    /// The executable to write. Defaults to the assembly name in the current directory, with the extension that
    /// executables have on the target system.
    /// </summary>
    public string? OutputPath { get; init; }

    public required TargetPlatform Target { get; init; }

    /// <summary>Exact version of the runtime packs; by default the newest patch of the application's framework.</summary>
    public string? RuntimeVersion { get; init; }

    public OptimizationMode Optimization { get; init; } = OptimizationMode.Blended;

    /// <summary>Overrides <c>System.Globalization.Invariant</c>; invariant mode removes the dependency on ICU.</summary>
    public bool? InvariantGlobalization { get; init; }

    /// <summary>Keep a symbol table in the executable for debuggers and profilers. Only Linux executables have one.</summary>
    public bool EmitSymbolTable { get; init; }

    /// <summary>Produce an executable that can be loaded at any address, so that the system can randomize it.</summary>
    public bool PositionIndependent { get; init; } = true;

    /// <summary>Where the object file and the compiler response file are written.</summary>
    public string? IntermediateDirectory { get; init; }
}

/// <summary>The outcome of a successful build.</summary>
public sealed record BuildResult(
    string OutputPath, long OutputSize, string RuntimeVersion, TimeSpan ResolveTime, TimeSpan CompileTime, TimeSpan LinkTime, LinkResult Link);

/// <summary>
/// Turns a .NET application into a native executable in three steps: resolve the packs, compile IL to an object
/// file with ILCompiler, and link it with the runtime libraries using one of Ina's own linkers.
/// </summary>
public sealed class BuildPipeline(PackageStore packages, ILogger logger)
{
    /// <summary>
    /// Runtime libraries for Linux in link order. Entries marked optional do not exist for every architecture or
    /// version.
    /// </summary>
    private static readonly (string Name, bool Required)[] LinuxLibraries =
    [
        ("libbootstrapper.o", true),
        ("libRuntime.WorkstationGC.a", true),
        ("libeventpipe-disabled.a", true),
        ("libRuntime.VxsortDisabled.a", false),
        ("libstandalonegc-disabled.a", true),
        ("libaotminipal.a", true),
        ("libstdc++compat.a", true),
        ("libSystem.Native.a", true),
        ("libSystem.Globalization.Native.a", true),
        ("libSystem.IO.Compression.Native.a", true),
        // Zstandard is part of the runtime pack since .NET 11; it follows its only user in the link order.
        ("libzstd.a", false),
        ("libSystem.Net.Security.Native.a", true),
        ("libSystem.Security.Cryptography.Native.OpenSsl.a", true),
        ("libz.a", false),
        ("libbrotlienc.a", false),
        ("libbrotlidec.a", false),
        ("libbrotlicommon.a", false),
    ];

    /// <summary>The same for Windows, as listed by the NativeAOT build (Microsoft.NETCore.Native.Windows.targets).</summary>
    private static (string Name, bool Required)[] WindowsLibraries(OptimizationMode optimization) =>
    [
        ("bootstrapper.obj", true),
        ("Runtime.WorkstationGC.lib", true),
        ("eventpipe-disabled.lib", true),

        // The vectorized sort of the garbage collector is large, so it is only linked when optimizing for speed.
        (optimization == OptimizationMode.Speed ? "Runtime.VxsortEnabled.lib" : "Runtime.VxsortDisabled.lib", false),
        ("standalonegc-disabled.lib", true),
        ("aotminipal.lib", true),
        ("zlibstatic.lib", true),
        ("brotlicommon.lib", true),
        ("brotlienc.lib", true),
        ("brotlidec.lib", true),
        ("zstd_static.lib", false),
        ("System.Globalization.Native.Aot.lib", true),
        ("System.IO.Compression.Native.Aot.lib", true),
    ];

    public async Task<BuildResult> BuildAsync(BuildOptions options, CancellationToken cancellationToken = default)
    {
        using var activity = InaTelemetry.ActivitySource.StartActivity("ina.build");
        activity?.SetTag("ina.target", options.Target.RuntimeIdentifier);

        var app = AppAssembly.Load(options.AssemblyPath);
        if (!app.HasEntryPoint)
        {
            throw new InaException($"{app.Path} has no entry point. Ina compiles executable assemblies (OutputType=Exe).");
        }

        var frameworkVersion = app.FrameworkVersion
            ?? ParseRequestedFramework(options.RuntimeVersion)
            ?? throw new InaException($"Could not determine the target framework of {app.Path}. Pass --runtime-version.");

        var outputPath = Path.GetFullPath(options.OutputPath ?? app.Name + options.Target.ExecutableExtension);
        if (Directory.Exists(outputPath))
        {
            throw new InaException($"The output path {outputPath} is a directory. Choose a file name with --output.");
        }

        var intermediate = options.IntermediateDirectory ?? DefaultIntermediateDirectory(app, options.Target);
        Directory.CreateDirectory(intermediate);

        // 1. Packs
        var clock = Stopwatch.StartNew();
        RuntimePacks packs;
        using (InaTelemetry.ActivitySource.StartActivity("ina.resolve-packs"))
        {
            packs = await RuntimePacks.ResolveAsync(packages, options.Target, frameworkVersion, options.RuntimeVersion, logger, cancellationToken)
                .ConfigureAwait(false);
        }

        var resolveTime = clock.Elapsed;

        // 2. IL -> object file
        clock.Restart();
        var objectPath = Path.Combine(intermediate, app.Name + options.Target.ObjectExtension);
        using (InaTelemetry.ActivitySource.StartActivity("ina.compile"))
        {
            var ilcOptions = new IlcOptions
            {
                InputAssembly = app.Path,
                OutputObject = objectPath,
                Target = options.Target,
                References = CollectReferences(app, packs),
                Optimization = options.Optimization,
                RuntimeKnobs = EffectiveKnobs(app, options),
                RuntimeVersion = packs.RuntimeVersion,
                DirectPInvokeLists = packs.DirectPInvokeLists,
            };
            logger.LogInformation("Compiling {Assembly} for {Target}", Path.GetFileName(app.Path), options.Target.RuntimeIdentifier);
            await new IlcCompiler(logger).CompileAsync(packs.CompilerPath, ilcOptions, Path.Combine(intermediate, app.Name + ".ilc.rsp"), cancellationToken)
                .ConfigureAwait(false);
        }

        var compileTime = clock.Elapsed;

        // 3. Object file + runtime libraries -> executable
        clock.Restart();
        LinkResult link;
        using (InaTelemetry.ActivitySource.StartActivity("ina.link"))
        {
            logger.LogInformation("Linking {Output}", outputPath);
            try
            {
                IReadOnlyList<LinkInput> inputs = [LinkInput.FromFile(objectPath), .. CollectNativeLibraries(packs, options)];
                IReadOnlyList<string> exports = [IlcArguments.DiagnosticsSymbol(packs.RuntimeVersion)];
                link = options.Target.OperatingSystem == TargetOperatingSystem.Windows
                    ? LinkForWindows(app, options, outputPath, inputs, exports)
                    : ElfLinker.Link(
                        new LinkOptions
                        {
                            OutputPath = outputPath,
                            Architecture = options.Target.Architecture,
                            Imports = ImportLibrarySet.ForGlibc(options.Target.Architecture),
                            Inputs = inputs,
                            PositionIndependent = options.PositionIndependent,
                            EmitSymbolTable = options.EmitSymbolTable,
                            ExportedSymbols = exports,
                        },
                        logger);
            }
            catch (LinkerException e)
            {
                throw new InaException($"Linking failed:{Environment.NewLine}{e.Message}", e);
            }
        }

        var linkTime = clock.Elapsed;
        activity?.SetTag("ina.output.size", link.FileSize);
        return new BuildResult(outputPath, link.FileSize, packs.Version, resolveTime, compileTime, linkTime, link);
    }

    private LinkResult LinkForWindows(
        AppAssembly app, BuildOptions options, string outputPath, IReadOnlyList<LinkInput> inputs, IReadOnlyList<string> exports)
    {
        if (options.EmitSymbolTable)
        {
            logger.LogWarning("Windows executables carry no symbol table; --symbols is ignored for this target.");
        }

        return PeLinker.Link(
            new PeLinkOptions
            {
                OutputPath = outputPath,
                Architecture = options.Target.Architecture,
                Imports = ImportLibrarySet.ForWindows(),
                Inputs = inputs,
                Subsystem = app.IsWindowsApplication ? PeSubsystem.Windows : PeSubsystem.Console,
                DynamicBase = options.PositionIndependent,
                ExportedSymbols = exports,

                // The NativeAOT build passes these on the linker command line; the runtime locates the managed
                // code by the bounds of its part of the code section.
                MergedSections = new Dictionary<string, string> { [".managedcode"] = ".text", ["hydrated"] = ".bss" },
            },
            logger);
    }

    /// <summary>
    /// The assemblies the compiler may load: the framework of the target platform, and the assemblies that ship next
    /// to the application. Framework assemblies win over local copies, which exist in self-contained outputs but are
    /// built for a different runtime.
    /// </summary>
    private static List<string> CollectReferences(AppAssembly app, RuntimePacks packs)
    {
        var references = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFileName(app.Path) };
        foreach (var directory in new[] { packs.FrameworkDirectory, packs.NativeDirectory })
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.dll").Order(StringComparer.Ordinal))
            {
                if (names.Add(Path.GetFileName(file)))
                {
                    references.Add(file);
                }
            }
        }

        references.AddRange(app.LocalReferences.Where(file => names.Add(Path.GetFileName(file))));
        return references;
    }

    private static List<LinkInput> CollectNativeLibraries(RuntimePacks packs, BuildOptions options)
    {
        var libraries = options.Target.OperatingSystem == TargetOperatingSystem.Windows ? WindowsLibraries(options.Optimization) : LinuxLibraries;
        var inputs = new List<LinkInput>();
        foreach (var (name, required) in libraries)
        {
            var path = Path.Combine(packs.NativeDirectory, name);
            if (File.Exists(path))
            {
                inputs.Add(LinkInput.FromFile(path));
            }
            else if (required)
            {
                throw new InaException($"The runtime pack is missing {name} (looked in {packs.NativeDirectory}).");
            }
        }

        return inputs;
    }

    private static Dictionary<string, string> EffectiveKnobs(AppAssembly app, BuildOptions options)
    {
        var knobs = new Dictionary<string, string>(app.RuntimeKnobs, StringComparer.Ordinal);
        if (options.InvariantGlobalization is { } invariant)
        {
            knobs["System.Globalization.Invariant"] = invariant ? "true" : "false";
        }

        return knobs;
    }

    private static Version? ParseRequestedFramework(string? runtimeVersion) =>
        runtimeVersion is not null && Version.TryParse(runtimeVersion.Split('-')[0], out var version) ? new Version(version.Major, version.Minor) : null;

    /// <summary>A stable per-application directory under the temporary folder, so repeated builds reuse it.</summary>
    private static string DefaultIntermediateDirectory(AppAssembly app, TargetPlatform target)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(app.Path)))[..12];
        return Path.Combine(Path.GetTempPath(), "ina", $"{app.Name}-{hash}", target.RuntimeIdentifier);
    }
}
