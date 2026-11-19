using Ina.Compilation;
using Ina.Linker;

namespace Ina.Tests;

public class IlcArgumentsTests
{
    private static readonly TargetPlatform LinuxX64 = new(TargetOperatingSystem.Linux, TargetArchitecture.X64);

    private static IlcOptions Options => new()
    {
        InputAssembly = "/app/App.dll",
        OutputObject = "/obj/App.o",
        Target = LinuxX64,
        References = ["/packs/System.Runtime.dll", "/app/Library.dll"],
    };

    [Fact]
    public void InputComesFirstFollowedByOutputAndReferences()
    {
        var arguments = IlcArguments.Build(Options);

        Assert.Equal(["/app/App.dll", "-o:/obj/App.o", "-r:/packs/System.Runtime.dll", "-r:/app/Library.dll"], arguments.Take(4));
    }

    [Theory]
    [InlineData(TargetArchitecture.X64, "x64", "linux-x64")]
    [InlineData(TargetArchitecture.Arm64, "arm64", "linux-arm64")]
    public void TargetPlatformIsPassedToTheCompilerAndBakedIntoTheRuntime(TargetArchitecture architecture, string name, string runtimeIdentifier)
    {
        var arguments = IlcArguments.Build(Options with { Target = new TargetPlatform(TargetOperatingSystem.Linux, architecture) });

        Assert.Contains("--targetos:linux", arguments);
        Assert.Contains($"--targetarch:{name}", arguments);
        Assert.Contains($"--runtimeknob:RUNTIME_IDENTIFIER={runtimeIdentifier}", arguments);
    }

    [Fact]
    public void FrameworkNativeLibrariesAreCalledDirectlyBecauseTheyAreLinkedStatically()
    {
        var arguments = IlcArguments.Build(Options);

        Assert.Contains("--directpinvoke:System.Native", arguments);
        Assert.Contains("--directpinvoke:System.Globalization.Native", arguments);
        Assert.Contains("--initassembly:System.Private.CoreLib", arguments);
    }

    [Fact]
    public void DiagnosticsEntryPointAndStackTraceDataFollowTheCompilerOfDotNet11()
    {
        var arguments = IlcArguments.Build(Options);

        Assert.Contains("--export-dynamic-symbol:DotNetRuntimeContractDescriptor", arguments);
        Assert.Contains("--stacktracedata:frames", arguments);
        Assert.Equal("DotNetRuntimeContractDescriptor", IlcArguments.DiagnosticsSymbol(new Version(11, 0)));
    }

    [Fact]
    public void CompilerOfDotNet10GetsTheArgumentsItUnderstands()
    {
        var arguments = IlcArguments.Build(Options with { RuntimeVersion = new Version(10, 0, 14) });

        // .NET 10 exposes the runtime to diagnostic tools through a different symbol, and its --stacktracedata
        // switch takes no value.
        Assert.Contains("--export-dynamic-symbol:DotNetRuntimeDebugHeader", arguments);
        Assert.Contains("--stacktracedata", arguments);
        Assert.DoesNotContain("--stacktracedata:frames", arguments);
        Assert.Equal("DotNetRuntimeDebugHeader", IlcArguments.DiagnosticsSymbol(new Version(10, 0)));
    }

    [Fact]
    public void NativeAotFeatureDefaultsApply()
    {
        var arguments = IlcArguments.Build(Options);

        Assert.Contains("--feature:System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault=false", arguments);
        Assert.Contains("--runtimeknob:System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault=false", arguments);
        Assert.Contains("--feature:System.Diagnostics.Tracing.EventSource.IsSupported=false", arguments);
    }

    [Fact]
    public void ApplicationSettingsOverrideTheDefaults()
    {
        var arguments = IlcArguments.Build(Options with
        {
            RuntimeKnobs = new Dictionary<string, string>
            {
                ["System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault"] = "true",
                ["System.Globalization.Invariant"] = "true",
                ["System.GC.Concurrent"] = "0",
            },
        });

        Assert.Contains("--feature:System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault=true", arguments);
        Assert.DoesNotContain("--feature:System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault=false", arguments);
        Assert.Contains("--feature:System.Globalization.Invariant=true", arguments);

        // Non-boolean settings are runtime configuration only, not compile-time feature switches.
        Assert.Contains("--runtimeknob:System.GC.Concurrent=0", arguments);
        Assert.DoesNotContain(arguments, static a => a.StartsWith("--feature:System.GC.Concurrent", StringComparison.Ordinal));
    }

    [Fact]
    public void DynamicCodeCanNotBeEnabledInAheadOfTimeCompiledCode()
    {
        var arguments = IlcArguments.Build(Options with
        {
            RuntimeKnobs = new Dictionary<string, string> { ["System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported"] = "true" },
        });

        Assert.Contains("--feature:System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported=false", arguments);
        Assert.DoesNotContain("--feature:System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported=true", arguments);
    }

    [Theory]
    [InlineData(OptimizationMode.None, false, null, "none")]
    [InlineData(OptimizationMode.Blended, true, null, "generic")]
    [InlineData(OptimizationMode.Size, true, "--Os", "generic")]
    [InlineData(OptimizationMode.Speed, true, "--Ot", "generic")]
    public void OptimizationModeSelectsCompilerFlags(OptimizationMode mode, bool optimize, string? preference, string folding)
    {
        var arguments = IlcArguments.Build(Options with { Optimization = mode });

        Assert.Equal(optimize, arguments.Contains("-O"));
        Assert.Equal(preference == "--Os", arguments.Contains("--Os"));
        Assert.Equal(preference == "--Ot", arguments.Contains("--Ot"));
        Assert.Contains($"--methodbodyfolding:{folding}", arguments);
    }

    [Fact]
    public void DebugInformationIsOptIn()
    {
        Assert.DoesNotContain("-g", IlcArguments.Build(Options));
        Assert.Contains("-g", IlcArguments.Build(Options with { DebugInfo = true }));
    }

    [Fact]
    public void ArgumentsAreDeterministic()
    {
        var knobs = new Dictionary<string, string> { ["B"] = "true", ["A"] = "false" };
        var reversed = new Dictionary<string, string> { ["A"] = "false", ["B"] = "true" };

        Assert.Equal(IlcArguments.Build(Options with { RuntimeKnobs = knobs }), IlcArguments.Build(Options with { RuntimeKnobs = reversed }));
    }

    [Fact]
    public void ResponseFileQuotesArgumentsThatContainSpaces()
    {
        var lines = IlcArguments.ToResponseFileLines(["-r:/plain/path.dll", "-r:/my projects/app/Library.dll"]);

        Assert.Equal(["-r:/plain/path.dll", "\"-r:/my projects/app/Library.dll\""], lines);
    }
}
