namespace Ina.EndToEnd.Tests;

/// <summary>
/// <c>dotnet publish</c> with <c>PublishIna</c> for macOS. Like every publish of these tests it runs without a
/// native toolchain on the PATH, and it needs nothing of macOS either, so the executables are published on every
/// host. They are run where that is possible: on a Mac of the same architecture, or for x64 on a Mac with Rosetta.
/// </summary>
public class MacOSPublishTests(PublishEnvironment environment)
{
    private const string SkipReason = "Running the executable requires a Mac of the same architecture (or Rosetta for x64).";

    [Theory]
    [InlineData("osx-arm64", MachOFile.CpuTypeArm64)]
    [InlineData("osx-x64", MachOFile.CpuTypeX64)]
    public async Task RuntimeIdentifierOfMacOSProducesAMachOExecutable(string runtimeIdentifier, uint cpuType)
    {
        using var project = new SampleProject(environment, "HelloWorld", "HelloWorld.csproj", InaReference.Sdk);

        var publish = await project.PublishAsync("-r", runtimeIdentifier);

        Assert.True(publish.ExitCode == 0, publish.Output + publish.Error);
        Assert.Contains("Linked HelloWorld with Ina", publish.Output);
        Assert.Contains(runtimeIdentifier, publish.Output);
        Assert.Contains("macOS 12.0+", publish.Output);

        // The executable is all there is, as for Linux: no assemblies, no host, and no bundle with debug symbols.
        Assert.Equal(["HelloWorld"], Directory.GetFileSystemEntries(project.PublishDirectory).Select(Path.GetFileName));
        Assert.Equal(cpuType, MachOFile.Read(project.Executable).CpuType);

        Assert.SkipUnless(CanRun(runtimeIdentifier), SkipReason);
        var run = await TestEnvironment.RunAsync(project.Executable, "from", "publish");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal($"Hello, World!{Environment.NewLine}Arguments: from, publish{Environment.NewLine}", run.Output);
    }

    [Fact]
    public async Task PublishWithoutRuntimeIdentifierTargetsTheMacItRunsOn()
    {
        Assert.SkipUnless(TestEnvironment.CanRunMacOSArm64, "Publishing for the host and running the result requires a Mac with Apple silicon.");
        using var project = new SampleProject(
            environment, "HelloWorld", "HelloWorld.csproj", InaReference.Package, "<PublishAot>true</PublishAot><PublishIna>true</PublishIna>");

        var publish = await project.PublishAsync();

        Assert.True(publish.ExitCode == 0, publish.Output + publish.Error);
        Assert.Contains("Linked HelloWorld with Ina", publish.Output);
        Assert.Contains("osx-arm64", publish.Output);
        Assert.Equal($"Hello, World!{Environment.NewLine}", (await TestEnvironment.RunAsync(project.Executable)).Output);
    }

    [Fact]
    public async Task RuntimeFeaturesWorkInTheExecutablePublishedForMacOS()
    {
        using var project = new SampleProject(environment, "RuntimeTour", "RuntimeTour.csproj", InaReference.Sdk);

        var publish = await project.PublishAsync("-r", "osx-arm64");
        Assert.True(publish.ExitCode == 0, publish.Output + publish.Error);

        Assert.SkipUnless(TestEnvironment.CanRunMacOSArm64, SkipReason);
        var native = await TestEnvironment.RunAsync(project.Executable);
        var reference = await TestEnvironment.RunOnJitAsync(TestEnvironment.SampleAssembly("RuntimeTour", "RuntimeTour"));

        Assert.True(native.ExitCode == 0, native.Output + native.Error);
        Assert.Contains("All checks passed.", native.Output);
        Assert.Equal(CommonLines(reference.Output), CommonLines(native.Output));
        Assert.Contains("# dynamic code: False", native.Output);
        Assert.Contains("# server gc: False", native.Output);
    }

    [Theory]
    [InlineData("osx-arm64")]
    [InlineData("osx-x64")]
    public async Task RuntimeLibrariesFollowTheGarbageCollectorAndDiagnosticsSettings(string runtimeIdentifier)
    {
        // Each of these properties replaces a static library of the runtime with another flavor; one of them
        // exists for x64 only.
        using var project = new SampleProject(
            environment, "RuntimeTour", "RuntimeTour.csproj", InaReference.Sdk,
            "<PublishIna>true</PublishIna><ServerGarbageCollection>true</ServerGarbageCollection>" +
            "<EventSourceSupport>true</EventSourceSupport><OptimizationPreference>Speed</OptimizationPreference>");

        var publish = await project.PublishAsync("-r", runtimeIdentifier);
        Assert.True(publish.ExitCode == 0, publish.Output + publish.Error);

        Assert.SkipUnless(CanRun(runtimeIdentifier), SkipReason);
        var run = await TestEnvironment.RunAsync(project.Executable);
        Assert.True(run.ExitCode == 0, run.Output + run.Error);
        Assert.Contains("All checks passed.", run.Output);
        Assert.Contains("# server gc: True", run.Output);
    }

    [Fact]
    public async Task ProjectReferencesAndContentFilesArePublished()
    {
        using var project = new SampleProject(environment, "JsonReader", Path.Combine("JsonReader", "JsonReader.csproj"), InaReference.Sdk);

        var publish = await project.PublishAsync("-r", "osx-arm64");
        Assert.True(publish.ExitCode == 0, publish.Output + publish.Error);

        var files = Directory.GetFiles(project.PublishDirectory).Select(Path.GetFileName).ToList();
        Assert.Contains("JsonReader", files);
        Assert.Contains("catalog.json", files);
        Assert.DoesNotContain(files, static file => file!.EndsWith(".dll", StringComparison.Ordinal));

        Assert.SkipUnless(TestEnvironment.CanRunMacOSArm64, SkipReason);
        var native = await TestEnvironment.RunAsync(project.Executable);
        var reference = await TestEnvironment.RunOnJitAsync(TestEnvironment.SampleAssembly(Path.Combine("JsonReader", "JsonReader"), "JsonReader"));
        Assert.Equal(0, native.ExitCode);
        Assert.Equal(reference.Output, native.Output);
    }

    [Fact]
    public async Task AspNetCoreApplicationServesRequests()
    {
        // The sample sends requests to itself and prints the responses.
        using var project = new SampleProject(environment, "MinimalApi", "MinimalApi.csproj", InaReference.Sdk);

        var publish = await project.PublishAsync("-r", "osx-arm64");
        Assert.True(publish.ExitCode == 0, publish.Output + publish.Error);

        Assert.SkipUnless(TestEnvironment.CanRunMacOSArm64, SkipReason);
        var native = await TestEnvironment.RunAsync(project.Executable);
        var reference = await TestEnvironment.RunOnJitAsync(TestEnvironment.SampleAssembly("MinimalApi", "MinimalApi"));

        Assert.True(native.ExitCode == 0, native.Output + native.Error);
        Assert.Contains("POST /todos -> 201 /todos/3", native.Output);
        Assert.Equal(reference.Output, native.Output);

        // PublishIna alone has to keep the defaults that the web SDK applies to a project with PublishAot.
        Assert.Contains("Event sources: available", native.Output);
    }

    [Fact]
    public async Task ChangedLinkOptionsRelinkWithoutRecompiling()
    {
        using var project = new SampleProject(environment, "HelloWorld", "HelloWorld.csproj", InaReference.Sdk);

        var first = await project.PublishAsync("-r", "osx-arm64");
        Assert.True(first.ExitCode == 0, first.Output + first.Error);
        var stripped = MachOFile.Read(project.Executable);

        var second = await project.PublishAsync("-r", "osx-arm64", "-p:StripSymbols=false");
        Assert.True(second.ExitCode == 0, second.Output + second.Error);
        var withSymbols = MachOFile.Read(project.Executable);

        Assert.Equal(0, stripped.LocalSymbolCount);
        Assert.True(withSymbols.LocalSymbolCount > 0);

        // The object file of the first publish is still valid, so only the link is repeated.
        Assert.DoesNotContain("Generating native code", second.Output);
        Assert.Contains("Linked HelloWorld with Ina", second.Output);
    }

    [Fact]
    public async Task FixedAddressExecutableIsRejectedBeforeAnythingIsDownloaded()
    {
        using var project = new SampleProject(environment, "HelloWorld", "HelloWorld.csproj", InaReference.Sdk);

        var publish = await project.PublishAsync("-r", "osx-arm64", "-p:PositionIndependentExecutable=false");

        // macOS has no fixed-address executables, which Linux offers through the same property.
        Assert.NotEqual(0, publish.ExitCode);
        Assert.Contains("error INA1003", publish.Output);
        Assert.Contains("PositionIndependentExecutable", publish.Output);
        Assert.False(File.Exists(Path.Combine(project.ProjectDirectory, "obj", "project.assets.json")));
    }

    private static bool CanRun(string runtimeIdentifier) =>
        runtimeIdentifier == "osx-arm64" ? TestEnvironment.CanRunMacOSArm64 : TestEnvironment.CanRunMacOSX64;

    // Lines starting with '#' describe the execution mode; everything else must be identical.
    private static string[] CommonLines(string output) =>
        output.Split('\n').Where(static line => !line.StartsWith('#')).ToArray();
}
