using Ina.Compilation;
using Ina.Linker;

namespace Ina.EndToEnd.Tests;

/// <summary>
/// macOS executables. Building them needs nothing of macOS: neither Apple's linker nor the SDK, so the builds run
/// on every host. The executables are run where that is possible: on a Mac of the same architecture, or for x64
/// on a Mac with Rosetta.
/// </summary>
public class MacOSTests
{
    private const string SkipReason = "Running the executable requires a Mac of the same architecture (or Rosetta for x64).";

    private static TargetPlatform MacOS(TargetArchitecture architecture) => new(TargetOperatingSystem.MacOS, architecture);

    [Theory]
    [InlineData(TargetArchitecture.Arm64, 0x0100000c)]
    [InlineData(TargetArchitecture.X64, 0x01000007)]
    public async Task HelloWorldIsBuiltForMacOSOnAnyHost(TargetArchitecture architecture, uint cpuType)
    {
        using var directory = new OutputDirectory();

        var build = await TestEnvironment.BuildAsync(
            TestEnvironment.SampleAssembly("HelloWorld", "HelloWorld"), directory.Path, o => o with { Target = MacOS(architecture) });

        // The Mach-O header: magic number and processor type.
        var header = new byte[8];
        await using (var stream = File.OpenRead(build.OutputPath))
        {
            await stream.ReadExactlyAsync(header, TestContext.Current.CancellationToken);
        }

        Assert.Equal(0xfeedfacf, BitConverter.ToUInt32(header, 0));
        Assert.Equal(cpuType, BitConverter.ToUInt32(header, 4));
        Assert.Contains("/usr/lib/libSystem.B.dylib", build.Link.NeededLibraries);

        // The minimum system is the one that the runtime packs were compiled for: macOS 14 since .NET 11.
        Assert.Equal(["macOS 14.0"], build.Link.RequiredVersions);
        Assert.StartsWith("11.0.", build.RuntimeVersion);

        Assert.SkipUnless(TestEnvironment.CanRunMacOS(architecture), SkipReason);
        var run = await TestEnvironment.RunAsync(build.OutputPath, "from", "macOS");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal($"Hello, World!{Environment.NewLine}Arguments: from, macOS{Environment.NewLine}", run.Output);
        Assert.Empty(run.Error);
    }

    [Theory]
    [InlineData(TargetArchitecture.Arm64)]
    [InlineData(TargetArchitecture.X64)]
    public async Task ExecutableDependsOnlyOnLibrariesOfTheSystem(TargetArchitecture architecture)
    {
        using var directory = new OutputDirectory();

        var build = await TestEnvironment.BuildAsync(
            TestEnvironment.SampleAssembly("RuntimeTour", "RuntimeTour"), directory.Path, o => o with { Target = MacOS(architecture) });

        // The C library, the Objective-C runtime and frameworks that every Mac has; no C++ runtime and nothing
        // that would have to be installed.
        Assert.Contains("/usr/lib/libSystem.B.dylib", build.Link.NeededLibraries);
        Assert.All(build.Link.NeededLibraries, static library => Assert.True(
            library is "/usr/lib/libSystem.B.dylib" or "/usr/lib/libobjc.A.dylib" || library.StartsWith("/System/Library/Frameworks/", StringComparison.Ordinal),
            library));
        Assert.DoesNotContain(build.Link.ImportedSymbols, static symbol => symbol.StartsWith("__Z", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(TargetArchitecture.Arm64)]
    [InlineData(TargetArchitecture.X64)]
    public async Task RuntimeFeaturesWorkInTheNativeExecutable(TargetArchitecture architecture)
    {
        Assert.SkipUnless(TestEnvironment.CanRunMacOS(architecture), SkipReason);
        using var directory = new OutputDirectory();
        var assembly = TestEnvironment.SampleAssembly("RuntimeTour", "RuntimeTour");

        var build = await TestEnvironment.BuildAsync(assembly, directory.Path, o => o with { Target = MacOS(architecture) });
        var native = await TestEnvironment.RunAsync(build.OutputPath);
        var reference = await TestEnvironment.RunOnJitAsync(assembly);

        Assert.True(native.ExitCode == 0, native.Output + native.Error);
        Assert.DoesNotContain("FAIL", native.Output);
        Assert.Contains("All checks passed.", native.Output);

        // Lines starting with '#' describe the execution mode; everything else must be identical.
        Assert.Equal(CommonLines(reference.Output), CommonLines(native.Output));
        Assert.Contains("# dynamic code: False", native.Output);
    }

    [Theory]
    [InlineData(TargetArchitecture.Arm64)]
    [InlineData(TargetArchitecture.X64)]
    public async Task MultiAssemblyJsonApplicationBehavesLikeOnTheRegularRuntime(TargetArchitecture architecture)
    {
        Assert.SkipUnless(TestEnvironment.CanRunMacOS(architecture), SkipReason);
        using var directory = new OutputDirectory();
        var assembly = TestEnvironment.SampleAssembly(Path.Combine("JsonReader", "JsonReader"), "JsonReader");
        var data = Path.Combine(Path.GetDirectoryName(assembly)!, "catalog.json");

        var build = await TestEnvironment.BuildAsync(assembly, directory.Path, o => o with { Target = MacOS(architecture) });
        var native = await TestEnvironment.RunAsync(build.OutputPath, data);
        var reference = await TestEnvironment.RunOnJitAsync(assembly, data);

        Assert.Equal(0, native.ExitCode);
        Assert.Contains("抹茶ラテ", native.Output);
        Assert.Equal(reference.Output, native.Output);

        // Failures surface the same way: a message on standard error and a non-zero exit code.
        var broken = Path.Combine(directory.Path, "broken.json");
        await File.WriteAllTextAsync(broken, "{ \"name\": ", TestContext.Current.CancellationToken);
        var invalid = await TestEnvironment.RunAsync(build.OutputPath, broken);
        Assert.Equal(2, invalid.ExitCode);
        Assert.StartsWith("Invalid JSON:", invalid.Error);
    }

    [Theory]
    [InlineData(OptimizationMode.None, false)]
    [InlineData(OptimizationMode.Size, true)]
    [InlineData(OptimizationMode.Speed, false)]
    public async Task BuildOptionsProduceWorkingExecutables(OptimizationMode optimization, bool symbols)
    {
        Assert.SkipUnless(TestEnvironment.CanRunMacOSArm64, SkipReason);
        using var directory = new OutputDirectory();

        var build = await TestEnvironment.BuildAsync(
            TestEnvironment.SampleAssembly("HelloWorld", "HelloWorld"),
            directory.Path,
            o => o with { Target = MacOS(TargetArchitecture.Arm64), Optimization = optimization, EmitSymbolTable = symbols, InvariantGlobalization = true });
        var run = await TestEnvironment.RunAsync(build.OutputPath);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal($"Hello, World!{Environment.NewLine}", run.Output);
    }

    [Fact]
    public async Task FixedAddressExecutablesAreRejectedBeforeAnythingIsBuilt()
    {
        using var directory = new OutputDirectory();

        var error = await Assert.ThrowsAsync<InaException>(() => TestEnvironment.BuildAsync(
            TestEnvironment.SampleAssembly("HelloWorld", "HelloWorld"), directory.Path,
            static o => o with { Target = MacOS(TargetArchitecture.Arm64), PositionIndependent = false }));

        Assert.Contains("position independent", error.Message);
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "obj")));
    }

    [Fact]
    public async Task CommandLineBuildsForMacOSAndReportsTheMinimumSystem()
    {
        using var directory = new OutputDirectory();
        var output = Path.Combine(directory.Path, "hello");

        var result = await TestEnvironment.RunAsync(
            "dotnet", "exec", Path.Combine(AppContext.BaseDirectory, "ina.dll"), TestEnvironment.SampleAssembly("HelloWorld", "HelloWorld"),
            "--os", "macos", "--arch", "arm64", "-o", output, "--intermediate-dir", Path.Combine(directory.Path, "obj"));

        Assert.True(result.ExitCode == 0, result.Output + result.Error);
        Assert.Contains($"HelloWorld.dll -> {output}", result.Output);
        Assert.Contains("osx-arm64", result.Output);
        Assert.Contains("macOS 14.0+", result.Output);

        if (TestEnvironment.CanRunMacOSArm64)
        {
            Assert.Equal($"Hello, World!{Environment.NewLine}", (await TestEnvironment.RunAsync(output)).Output);
        }
    }

    [Fact]
    public async Task InaCompilesItselfAndTheNativeToolBuildsAWorkingApplication()
    {
        Assert.SkipUnless(TestEnvironment.CanRunMacOSArm64, SkipReason);

        // The tool downloads packages over HTTPS and hashes them.
        Assert.SkipUnless(TestEnvironment.MacOSImportsCoverCryptographyOfDotNet11, TestEnvironment.MacOSImportsSkipReason);
        using var directory = new OutputDirectory();
        var hello = Path.Combine(directory.Path, "hello");
        var target = MacOS(TargetArchitecture.Arm64);

        // The tool exercises far more of the runtime than the samples: HTTP, zip, JSON, hashing, metadata reading.
        var native = await TestEnvironment.BuildAsync(TestEnvironment.InaAssembly, Path.Combine(directory.Path, "tool"), o => o with { Target = target });
        var build = await TestEnvironment.RunAsync(
            native.OutputPath, TestEnvironment.SampleAssembly("HelloWorld", "HelloWorld"), "--os", "macos", "--arch", "arm64", "-o", hello,
            "--intermediate-dir", Path.Combine(directory.Path, "obj"));

        Assert.True(build.ExitCode == 0, build.Output + build.Error);
        Assert.Contains($"HelloWorld.dll -> {hello}", build.Output);

        var run = await TestEnvironment.RunAsync(hello, "self", "hosted");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal($"Hello, World!{Environment.NewLine}Arguments: self, hosted{Environment.NewLine}", run.Output);

        // The native tool reports errors like the managed one.
        var missing = await TestEnvironment.RunAsync(native.OutputPath, "does-not-exist.dll");
        Assert.Equal(1, missing.ExitCode);
        Assert.Contains("error: Assembly not found", missing.Error);
    }

    private static string[] CommonLines(string output) =>
        output.Split('\n').Where(static line => !line.StartsWith('#')).ToArray();
}
