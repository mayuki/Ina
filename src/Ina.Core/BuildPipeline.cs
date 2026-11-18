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

    /// <summary>The executable to write. Defaults to the assembly name in the current directory.</summary>
    public string? OutputPath { get; init; }

    public required TargetPlatform Target { get; init; }

    /// <summary>Exact version of the runtime packs; by default the newest patch of the application's framework.</summary>
    public string? RuntimeVersion { get; init; }

    public OptimizationMode Optimization { get; init; } = OptimizationMode.Blended;

    /// <summary>Overrides <c>System.Globalization.Invariant</c>; invariant mode removes the dependency on ICU.</summary>
    public bool? InvariantGlobalization { get; init; }

    /// <summary>Keep a symbol table in the executable for debuggers and profilers.</summary>
    public bool EmitSymbolTable { get; init; }

    /// <summary>Produce a position independent executable.</summary>
    public bool PositionIndependent { get; init; } = true;

    /// <summary>Where the object file and the compiler response file are written.</summary>
    public string? IntermediateDirectory { get; init; }
}

/// <summary>The outcome of a successful build.</summary>
public sealed record BuildResult(
    string OutputPath, long OutputSize, string RuntimeVersion, TimeSpan ResolveTime, TimeSpan CompileTime, TimeSpan LinkTime, LinkResult Link);

/// <summary>
/// Turns a .NET application into a native executable in three steps: resolve the packs, compile IL to an object
/// file with ILCompiler, and link it with the runtime libraries using Ina's own linker.
/// </summary>
public sealed class BuildPipeline(PackageStore packages, ILogger logger)
{
    /// <summary>Runtime libraries in link order. Entries marked optional do not exist for every architecture.</summary>
    private static readonly (string Name, bool Required)[] NativeLibraries =
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
        ("libSystem.Net.Security.Native.a", true),
        ("libSystem.Security.Cryptography.Native.OpenSsl.a", true),
        ("libz.a", false),
        ("libbrotlienc.a", false),
        ("libbrotlidec.a", false),
        ("libbrotlicommon.a", false),
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

        var outputPath = Path.GetFullPath(options.OutputPath ?? app.Name);
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
        var objectPath = Path.Combine(intermediate, app.Name + ".o");
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
                link = ElfLinker.Link(
                    new LinkOptions
                    {
                        OutputPath = outputPath,
                        Architecture = options.Target.Architecture,
                        Imports = ImportLibrarySet.ForGlibc(options.Target.Architecture),
                        Inputs = [LinkInput.FromFile(objectPath), .. CollectNativeLibraries(packs)],
                        PositionIndependent = options.PositionIndependent,
                        EmitSymbolTable = options.EmitSymbolTable,
                        ExportedSymbols = [IlcArguments.DebugHeaderSymbol],
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

    private static List<LinkInput> CollectNativeLibraries(RuntimePacks packs)
    {
        var inputs = new List<LinkInput>();
        foreach (var (name, required) in NativeLibraries)
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
