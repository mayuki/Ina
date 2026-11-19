namespace Ina.EndToEnd.Tests;

/// <summary>How the properties of a project shape a publish with Ina, and what is rejected.</summary>
public class PublishConfigurationTests(PublishEnvironment environment)
{
    [Fact]
    public async Task RuntimeLibrariesFollowTheGarbageCollectorAndDiagnosticsSettings()
    {
        Assert.SkipUnless(TestEnvironment.CanRunLinuxX64, "Running the executable requires an x86-64 Linux host.");

        // Each of these properties replaces a static library of the runtime with another flavor.
        using var project = new SampleProject(
            environment, "RuntimeTour", "RuntimeTour.csproj", InaReference.Sdk,
            "<PublishIna>true</PublishIna><ServerGarbageCollection>true</ServerGarbageCollection>" +
            "<EventSourceSupport>true</EventSourceSupport><OptimizationPreference>Speed</OptimizationPreference>");

        var publish = await project.PublishAsync("-r", "linux-x64");
        Assert.True(publish.ExitCode == 0, publish.Output + publish.Error);

        var run = await TestEnvironment.RunAsync(project.Executable);
        Assert.True(run.ExitCode == 0, run.Output + run.Error);
        Assert.Contains("All checks passed.", run.Output);
        Assert.Contains("# server gc: True", run.Output);
    }

    [Fact]
    public async Task ChangedLinkOptionsRelinkWithoutRecompiling()
    {
        using var project = new SampleProject(environment, "HelloWorld", "HelloWorld.csproj", InaReference.Sdk);

        var first = await project.PublishAsync("-r", "linux-x64");
        Assert.True(first.ExitCode == 0, first.Output + first.Error);
        var positionIndependent = ElfFile.Read(project.Executable);

        var second = await project.PublishAsync("-r", "linux-x64", "-p:PositionIndependentExecutable=false", "-p:StripSymbols=false");
        Assert.True(second.ExitCode == 0, second.Output + second.Error);
        var fixedAddress = ElfFile.Read(project.Executable);

        Assert.Equal(ElfFile.TypeSharedObject, positionIndependent.Type);
        Assert.False(positionIndependent.HasSymbolTable);
        Assert.Equal(ElfFile.TypeExecutable, fixedAddress.Type);
        Assert.True(fixedAddress.HasSymbolTable);

        // The object file of the first publish is still valid, so only the link is repeated.
        Assert.DoesNotContain("Generating native code", second.Output);
        Assert.Contains("Linked HelloWorld with Ina", second.Output);
    }

    [Fact]
    public async Task PackageReferenceWithoutPublishAotIsRejected()
    {
        // A package cannot influence the restore of the project that refers to it, so it cannot imply PublishAot.
        using var project = new SampleProject(environment, "HelloWorld", "HelloWorld.csproj", InaReference.Package);

        var publish = await project.PublishAsync("-r", "linux-x64");

        Assert.NotEqual(0, publish.ExitCode);
        Assert.Contains("error INA1001", publish.Output);
        Assert.Contains("PublishAot", publish.Output);
        Assert.False(Directory.Exists(project.PublishDirectory) && Directory.EnumerateFiles(project.PublishDirectory).Any());
    }

    [Theory]
    [InlineData("win-x64")]
    [InlineData("linux-musl-arm64")]
    public async Task UnsupportedRuntimeIdentifierIsRejectedBeforeAnythingIsDownloaded(string runtimeIdentifier)
    {
        using var project = new SampleProject(environment, "HelloWorld", "HelloWorld.csproj", InaReference.Sdk);

        var publish = await project.PublishAsync("-r", runtimeIdentifier);

        Assert.NotEqual(0, publish.ExitCode);
        Assert.Contains("error INA1002", publish.Output);
        Assert.Contains(runtimeIdentifier, publish.Output);
        Assert.Contains("linux-x64, linux-arm64", publish.Output);

        // The restore, which would fetch the compiler and the runtime pack of that platform, has not happened.
        Assert.False(File.Exists(Path.Combine(project.ProjectDirectory, "obj", "project.assets.json")));
    }

    [Theory]
    [InlineData("StaticExecutable", "true", "INA1003")]
    [InlineData("NativeLib", "Shared", "INA1003")]
    [InlineData("TargetFramework", "net9.0", "INA1004")]
    public async Task SettingsThatInaCannotHonorAreRejected(string property, string value, string code)
    {
        using var project = new SampleProject(
            environment, "HelloWorld", "HelloWorld.csproj", InaReference.Sdk, $"<PublishIna>true</PublishIna><{property}>{value}</{property}>");

        var publish = await project.PublishAsync("-r", "linux-x64");

        Assert.NotEqual(0, publish.ExitCode);
        Assert.Contains($"error {code}", publish.Output);
        Assert.Contains(property, publish.Output);
        Assert.False(File.Exists(Path.Combine(project.ProjectDirectory, "obj", "project.assets.json")));
    }
}
