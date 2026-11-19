using System.CommandLine;
using System.Diagnostics;
using Ina;
using Ina.Cli;
using Ina.Compilation;
using Ina.Diagnostics;
using Ina.Packs;
using Microsoft.Extensions.Logging;

var assemblyArgument = new Argument<string>("assembly")
{
    Description = "The application to compile: a .NET assembly with an entry point, e.g. bin/Release/net11.0/App.dll",
};
var outputOption = new Option<string?>("--output", "-o")
{
    Description = "Path of the executable to write [default: the assembly name in the current directory, with .exe for Windows]",
};
var osOption = new Option<string?>("--os")
{
    Description = "Target operating system: linux or windows [default: the system of this machine if it is one of them, otherwise linux]",
};
osOption.AcceptOnlyFromAmong("linux", "windows");
var archOption = new Option<string?>("--arch")
{
    Description = "Target architecture: x64 or arm64 [default: the architecture of this machine]",
};
archOption.AcceptOnlyFromAmong("x64", "arm64");
var optimizeOption = new Option<string>("--optimize", "-O")
{
    Description = "Optimization preference: blended, speed, size or none",
    DefaultValueFactory = static _ => "blended",
};
optimizeOption.AcceptOnlyFromAmong("blended", "speed", "size", "none");
var runtimeVersionOption = new Option<string?>("--runtime-version")
{
    Description = "Version of the .NET runtime packs to compile against, e.g. 11.0.1 [default: newest patch of the app's framework]",
};
var invariantOption = new Option<bool>("--invariant-globalization")
{
    Description = "Use culture-invariant globalization so that the executable does not need ICU at run time",
};
var symbolsOption = new Option<bool>("--symbols")
{
    Description = "Keep a symbol table in the executable (useful for debuggers and profilers; Linux targets only)",
};
var noPieOption = new Option<bool>("--no-pie")
{
    Description = "Produce a fixed-address executable instead of one that the system can load at a random address",
};
var intermediateOption = new Option<string?>("--intermediate-dir")
{
    Description = "Directory for intermediate files (object file, compiler response file)",
};
var verboseOption = new Option<bool>("--verbose", "-v")
{
    Description = "Show detailed progress, timings and compiler output",
};

var root = new RootCommand("Ina (IL to NAtive) compiles a .NET application to a native executable without a native toolchain.")
{
    assemblyArgument, outputOption, osOption, archOption, optimizeOption, runtimeVersionOption, invariantOption, symbolsOption, noPieOption,
    intermediateOption, verboseOption,
};

root.SetAction(async (parseResult, cancellationToken) =>
{
    var verbose = parseResult.GetValue(verboseOption);
    var logger = new ConsoleReporter(verbose);

    // Every pipeline phase is an activity; in verbose mode their durations are printed as they finish.
    using var listener = new ActivityListener
    {
        ShouldListenTo = static source => source.Name == InaTelemetry.SourceName,
        Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        ActivityStopped = activity => logger.LogDebug("{Phase} took {Duration:F0} ms", activity.OperationName, activity.Duration.TotalMilliseconds),
    };
    ActivitySource.AddActivityListener(listener);

    try
    {
        var options = new BuildOptions
        {
            AssemblyPath = parseResult.GetValue(assemblyArgument)!,
            OutputPath = parseResult.GetValue(outputOption),
            Target = TargetPlatform.Parse(parseResult.GetValue(osOption), parseResult.GetValue(archOption)),
            RuntimeVersion = parseResult.GetValue(runtimeVersionOption),
            Optimization = parseResult.GetValue(optimizeOption) switch
            {
                "speed" => OptimizationMode.Speed,
                "size" => OptimizationMode.Size,
                "none" => OptimizationMode.None,
                _ => OptimizationMode.Blended,
            },
            InvariantGlobalization = parseResult.GetValue(invariantOption) ? true : null,
            EmitSymbolTable = parseResult.GetValue(symbolsOption),
            PositionIndependent = !parseResult.GetValue(noPieOption),
            IntermediateDirectory = parseResult.GetValue(intermediateOption),
        };

        using var packages = PackageStore.CreateDefault(logger);
        var result = await new BuildPipeline(packages, logger).BuildAsync(options, cancellationToken);

        logger.LogDebug("Dependencies: {Libraries}", string.Join(", ", result.Link.NeededLibraries));
        logger.LogDebug(
            "Linked {Objects} objects, {Imports} imported symbols, {Dropped} unused sections dropped",
            result.Link.ObjectCount, result.Link.ImportedSymbols.Count, result.Link.DiscardedSectionCount);
        Console.Out.WriteLine(
            $"{Path.GetFileName(options.AssemblyPath)} -> {result.OutputPath} " +
            $"({result.OutputSize / 1024.0 / 1024.0:F1} MB, {options.Target.RuntimeIdentifier}, .NET {result.RuntimeVersion}" +
            $"{(result.Link.MinimumGlibcVersion is { } glibc ? $", glibc {glibc}+" : string.Empty)}, " +
            $"compile {result.CompileTime.TotalSeconds:F1}s, link {result.LinkTime.TotalSeconds:F2}s)");
        return 0;
    }
    catch (InaException e)
    {
        logger.LogError("{Message}", e.Message);
        return 1;
    }
    catch (OperationCanceledException)
    {
        return 130;
    }
});

return await root.Parse(args).InvokeAsync();
