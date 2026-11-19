namespace Ina.EndToEnd.Tests;

/// <summary>
/// <c>dotnet publish</c> with <c>PublishIna</c> for Windows. Like every publish of these tests it runs without a
/// native toolchain: on Windows the publish needs neither Visual Studio nor the Windows SDK, and on other hosts
/// nothing of Windows either, so the executables are published on every host. They are run on a Windows machine
/// of their architecture.
/// </summary>
public class WindowsPublishTests(PublishEnvironment environment)
{
    private const string SkipReason = "Running the executable requires an x64 Windows host.";
    private const string SkipReasonArm64 = "Running the executable requires a Windows on ARM64 host.";

    [Theory]
    [InlineData("win-x64", PeFile.MachineX64)]
    [InlineData("win-arm64", PeFile.MachineArm64)]
    public async Task RuntimeIdentifierOfWindowsProducesAPeExecutable(string runtimeIdentifier, ushort machine)
    {
        using var project = new SampleProject(environment, "HelloWorld", "HelloWorld.csproj", InaReference.Sdk);

        var publish = await project.PublishAsync("-r", runtimeIdentifier);

        Assert.True(publish.ExitCode == 0, publish.Output + publish.Error);
        Assert.Contains("Linked HelloWorld.exe with Ina", publish.Output);
        Assert.Contains(runtimeIdentifier, publish.Output);

        // The executable is all there is, as for the other systems: no assemblies, no host, and no program database.
        Assert.Equal(["HelloWorld.exe"], Directory.GetFileSystemEntries(project.PublishDirectory).Select(Path.GetFileName));

        // A relocatable console application, like the one link.exe produces, that exports what diagnostic tools
        // look up: the name comes from the module-definition file of ILCompiler.
        var image = PeFile.Read(project.WindowsExecutable);
        Assert.Equal(machine, image.Machine);
        Assert.Equal(PeFile.SubsystemConsole, image.Subsystem);
        Assert.True(image.IsRelocatable);
        Assert.Equal(["DotNetRuntimeContractDescriptor"], image.Exports);

        Assert.SkipUnless(CanRun(runtimeIdentifier), runtimeIdentifier == "win-x64" ? SkipReason : SkipReasonArm64);
        var run = await TestEnvironment.RunAsync(project.WindowsExecutable, "from", "publish");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal($"Hello, World!{Environment.NewLine}Arguments: from, publish{Environment.NewLine}", run.Output);
    }

    [Fact]
    public async Task PublishWithoutRuntimeIdentifierTargetsTheWindowsMachineItRunsOn()
    {
        Assert.SkipUnless(TestEnvironment.CanRunWindowsX64, "Publishing for the host and running the result requires an x64 Windows host.");
        using var project = new SampleProject(
            environment, "HelloWorld", "HelloWorld.csproj", InaReference.Package, "<PublishAot>true</PublishAot><PublishIna>true</PublishIna>");

        var publish = await project.PublishAsync();

        Assert.True(publish.ExitCode == 0, publish.Output + publish.Error);
        Assert.Contains("Linked HelloWorld.exe with Ina", publish.Output);
        Assert.Contains("win-x64", publish.Output);
        Assert.Equal($"Hello, World!{Environment.NewLine}", (await TestEnvironment.RunAsync(project.WindowsExecutable)).Output);
    }

    [Fact]
    public async Task RuntimeFeaturesWorkInTheExecutablePublishedForWindows()
    {
        using var project = new SampleProject(environment, "RuntimeTour", "RuntimeTour.csproj", InaReference.Sdk);

        var publish = await project.PublishAsync("-r", "win-x64");
        Assert.True(publish.ExitCode == 0, publish.Output + publish.Error);

        Assert.SkipUnless(TestEnvironment.CanRunWindowsX64, SkipReason);
        var native = await TestEnvironment.RunAsync(project.WindowsExecutable);
        var reference = await TestEnvironment.RunOnJitAsync(TestEnvironment.SampleAssembly("RuntimeTour", "RuntimeTour"));

        // Exceptions, garbage collection, threads and thread statics exercise the function table, the thread-local
        // storage and the imports that the linker produced.
        Assert.True(native.ExitCode == 0, native.Output + native.Error);
        Assert.Contains("All checks passed.", native.Output);
        Assert.Equal(CommonLines(reference.Output), CommonLines(native.Output));
        Assert.Contains("# dynamic code: False", native.Output);
        Assert.Contains("# server gc: False", native.Output);
    }

    [Theory]
    [InlineData("win-x64")]
    [InlineData("win-arm64")]
    public async Task RuntimeLibrariesFollowTheGarbageCollectorAndDiagnosticsSettings(string runtimeIdentifier)
    {
        // Each of these properties replaces a static library of the runtime with another flavor.
        using var project = new SampleProject(
            environment, "RuntimeTour", "RuntimeTour.csproj", InaReference.Sdk,
            "<PublishIna>true</PublishIna><ServerGarbageCollection>true</ServerGarbageCollection>" +
            "<EventSourceSupport>true</EventSourceSupport><OptimizationPreference>Speed</OptimizationPreference>");

        var publish = await project.PublishAsync("-r", runtimeIdentifier);
        Assert.True(publish.ExitCode == 0, publish.Output + publish.Error);

        Assert.SkipUnless(CanRun(runtimeIdentifier), runtimeIdentifier == "win-x64" ? SkipReason : SkipReasonArm64);
        var run = await TestEnvironment.RunAsync(project.WindowsExecutable);
        Assert.True(run.ExitCode == 0, run.Output + run.Error);
        Assert.Contains("All checks passed.", run.Output);
        Assert.Contains("# server gc: True", run.Output);
    }

    [Fact]
    public async Task ProjectReferencesAndContentFilesArePublished()
    {
        using var project = new SampleProject(environment, "JsonReader", Path.Combine("JsonReader", "JsonReader.csproj"), InaReference.Sdk);

        var publish = await project.PublishAsync("-r", "win-x64");
        Assert.True(publish.ExitCode == 0, publish.Output + publish.Error);

        var files = Directory.GetFiles(project.PublishDirectory).Select(Path.GetFileName).ToList();
        Assert.Contains("JsonReader.exe", files);
        Assert.Contains("catalog.json", files);
        Assert.DoesNotContain(files, static file => file!.EndsWith(".dll", StringComparison.Ordinal));

        Assert.SkipUnless(TestEnvironment.CanRunWindowsX64, SkipReason);
        var native = await TestEnvironment.RunAsync(project.WindowsExecutable);
        var reference = await TestEnvironment.RunOnJitAsync(TestEnvironment.SampleAssembly(Path.Combine("JsonReader", "JsonReader"), "JsonReader"));
        Assert.Equal(0, native.ExitCode);
        Assert.Equal(reference.Output, native.Output);
    }

    [Fact]
    public async Task AspNetCoreApplicationServesRequests()
    {
        // The sample sends requests to itself and prints the responses.
        using var project = new SampleProject(environment, "MinimalApi", "MinimalApi.csproj", InaReference.Sdk);

        var publish = await project.PublishAsync("-r", "win-x64");
        Assert.True(publish.ExitCode == 0, publish.Output + publish.Error);

        Assert.SkipUnless(TestEnvironment.CanRunWindowsX64, SkipReason);
        var native = await TestEnvironment.RunAsync(project.WindowsExecutable);
        var reference = await TestEnvironment.RunOnJitAsync(TestEnvironment.SampleAssembly("MinimalApi", "MinimalApi"));

        Assert.True(native.ExitCode == 0, native.Output + native.Error);
        Assert.Contains("POST /todos -> 201 /todos/3", native.Output);
        Assert.Equal(reference.Output, native.Output);
        Assert.Contains("Event sources: available", native.Output);
    }

    [Fact]
    public async Task GraphicalApplicationIsLinkedForTheWindowsSubsystem()
    {
        // The NativeAOT targets derive the subsystem from the output type, which Ina takes as it is.
        using var project = new SampleProject(
            environment, "HelloWorld", "HelloWorld.csproj", InaReference.Sdk, "<PublishIna>true</PublishIna><OutputType>WinExe</OutputType>");

        var publish = await project.PublishAsync("-r", "win-x64");

        Assert.True(publish.ExitCode == 0, publish.Output + publish.Error);
        Assert.Equal(PeFile.SubsystemWindows, PeFile.Read(project.WindowsExecutable).Subsystem);
    }

    [Fact]
    public async Task ChangedLinkOptionsRelinkWithoutRecompiling()
    {
        using var project = new SampleProject(environment, "HelloWorld", "HelloWorld.csproj", InaReference.Sdk);

        var first = await project.PublishAsync("-r", "win-x64");
        Assert.True(first.ExitCode == 0, first.Output + first.Error);
        var relocatable = PeFile.Read(project.WindowsExecutable);

        // As for Linux, the property asks for an image that is bound to its preferred address.
        var second = await project.PublishAsync("-r", "win-x64", "-p:PositionIndependentExecutable=false");
        Assert.True(second.ExitCode == 0, second.Output + second.Error);
        var fixedAddress = PeFile.Read(project.WindowsExecutable);

        Assert.True(relocatable.IsRelocatable);
        Assert.False(fixedAddress.IsRelocatable);

        // The object file of the first publish is still valid, so only the link is repeated.
        Assert.DoesNotContain("Generating native code", second.Output);
        Assert.Contains("Linked HelloWorld.exe with Ina", second.Output);
    }

    [Theory]
    [InlineData("ControlFlowGuard", "Guard")]
    [InlineData("LinkerSubsystem", "NATIVE")]
    public async Task SettingsThatInaCannotHonorForWindowsAreRejectedBeforeAnythingIsDownloaded(string property, string value)
    {
        using var project = new SampleProject(
            environment, "HelloWorld", "HelloWorld.csproj", InaReference.Sdk, $"<PublishIna>true</PublishIna><{property}>{value}</{property}>");

        var publish = await project.PublishAsync("-r", "win-x64");

        Assert.NotEqual(0, publish.ExitCode);
        Assert.Contains("error INA1003", publish.Output);
        Assert.Contains($"{property}={value}", publish.Output);
        Assert.False(File.Exists(Path.Combine(project.ProjectDirectory, "obj", "project.assets.json")));
    }

    private static bool CanRun(string runtimeIdentifier) =>
        runtimeIdentifier == "win-x64" ? TestEnvironment.CanRunWindowsX64 : TestEnvironment.CanRunWindowsArm64;

    // Lines starting with '#' describe the execution mode; everything else must be identical.
    private static string[] CommonLines(string output) =>
        output.Split('\n').Where(static line => !line.StartsWith('#')).ToArray();
}
