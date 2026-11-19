namespace Ina.EndToEnd.Tests;

/// <summary>
/// <c>dotnet publish</c> with <c>PublishIna</c>: the .NET SDK compiles the application the way it does for
/// NativeAOT, and Ina links the result. Every publish in these tests runs without a native toolchain on the PATH.
/// </summary>
public class PublishTests(PublishEnvironment environment)
{
    [Fact]
    public async Task SdkReferenceAndPublishInaProduceANativeExecutable()
    {
        Assert.SkipUnless(TestEnvironment.CanRunLinuxX64, "Publishing for the host and running the result requires an x86-64 Linux host.");
        using var project = new SampleProject(environment, "HelloWorld", "HelloWorld.csproj", InaReference.Sdk);

        // No runtime identifier: like PublishAot, PublishIna publishes for the machine it runs on.
        var publish = await project.PublishAsync();

        Assert.True(publish.ExitCode == 0, publish.Output + publish.Error);
        Assert.Contains("Linked HelloWorld with Ina", publish.Output);
        Assert.Contains("linux-x64", publish.Output);

        // The executable is all there is: no assemblies, no host, no runtime configuration.
        Assert.Equal(["HelloWorld"], Directory.GetFiles(project.PublishDirectory).Select(Path.GetFileName));
        var run = await TestEnvironment.RunAsync(project.Executable, "from", "publish");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal($"Hello, World!{Environment.NewLine}Arguments: from, publish{Environment.NewLine}", run.Output);
    }

    [Fact]
    public async Task PackageReferencePublishesWhenPublishAotIsSetAsWell()
    {
        Assert.SkipUnless(TestEnvironment.CanRunLinuxX64, "Running the executable requires an x86-64 Linux host.");
        using var project = new SampleProject(
            environment, "HelloWorld", "HelloWorld.csproj", InaReference.Package, "<PublishAot>true</PublishAot><PublishIna>true</PublishIna>");

        var publish = await project.PublishAsync("-r", "linux-x64");

        Assert.True(publish.ExitCode == 0, publish.Output + publish.Error);
        Assert.Contains("Linked HelloWorld with Ina", publish.Output);
        Assert.Equal(["HelloWorld"], Directory.GetFiles(project.PublishDirectory).Select(Path.GetFileName));
        Assert.Equal($"Hello, World!{Environment.NewLine}", (await TestEnvironment.RunAsync(project.Executable)).Output);
    }

    [Fact]
    public async Task RuntimeFeaturesWorkInThePublishedExecutable()
    {
        Assert.SkipUnless(TestEnvironment.CanRunLinuxX64, "Running the executable requires an x86-64 Linux host.");
        using var project = new SampleProject(environment, "RuntimeTour", "RuntimeTour.csproj", InaReference.Sdk);

        var publish = await project.PublishAsync("-r", "linux-x64");
        Assert.True(publish.ExitCode == 0, publish.Output + publish.Error);

        var native = await TestEnvironment.RunAsync(project.Executable);
        var reference = await TestEnvironment.RunOnJitAsync(TestEnvironment.SampleAssembly("RuntimeTour", "RuntimeTour"));

        Assert.True(native.ExitCode == 0, native.Output + native.Error);
        Assert.Contains("All checks passed.", native.Output);
        Assert.Equal(CommonLines(reference.Output), CommonLines(native.Output));
        Assert.Contains("# dynamic code: False", native.Output);
        Assert.Contains("# server gc: False", native.Output);
    }

    [Fact]
    public async Task ProjectReferencesAndContentFilesArePublished()
    {
        Assert.SkipUnless(TestEnvironment.CanRunLinuxX64, "Running the executable requires an x86-64 Linux host.");

        // "enable" is accepted as a spelling of the switch, like the values of Nullable.
        using var project = new SampleProject(
            environment, "JsonReader", Path.Combine("JsonReader", "JsonReader.csproj"), InaReference.Sdk, "<PublishIna>enable</PublishIna>");

        var publish = await project.PublishAsync("-r", "linux-x64");
        Assert.True(publish.ExitCode == 0, publish.Output + publish.Error);

        // The library of the referenced project is compiled into the executable; the data file is copied next to it,
        // which is where the application looks when it gets no argument.
        var files = Directory.GetFiles(project.PublishDirectory).Select(Path.GetFileName).ToList();
        Assert.Contains("JsonReader", files);
        Assert.Contains("catalog.json", files);
        Assert.DoesNotContain(files, static file => file!.EndsWith(".dll", StringComparison.Ordinal));
        var native = await TestEnvironment.RunAsync(project.Executable);
        var assembly = TestEnvironment.SampleAssembly(Path.Combine("JsonReader", "JsonReader"), "JsonReader");
        var reference = await TestEnvironment.RunOnJitAsync(assembly);

        Assert.Equal(0, native.ExitCode);
        Assert.Contains("Most expensive: Mechanical Keyboard", native.Output);
        Assert.Equal(reference.Output, native.Output);
    }

    [Fact]
    public async Task RuntimeIdentifierOfAnotherArchitectureCrossCompiles()
    {
        // The switch can also come from the command line, for a project that only refers to the SDK.
        using var project = new SampleProject(environment, "HelloWorld", "HelloWorld.csproj", InaReference.Sdk, properties: string.Empty);

        var publish = await project.PublishAsync("-r", "linux-arm64", "-p:PublishIna=true");

        Assert.True(publish.ExitCode == 0, publish.Output + publish.Error);
        Assert.Contains("linux-arm64", publish.Output);
        Assert.Equal(ElfFile.MachineAArch64, ElfFile.Read(project.Executable).Machine);

        Assert.SkipUnless(TestEnvironment.CanRunLinuxArm64, "Running the executable requires an AArch64 Linux host, or qemu-user with an AArch64 C library.");
        var run = await TestEnvironment.RunArm64Async(project.Executable, "from", "arm64");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal($"Hello, World!{Environment.NewLine}Arguments: from, arm64{Environment.NewLine}", run.Output);
    }

    [Fact]
    public async Task ProjectWithoutPublishInaIsPublishedAsUsual()
    {
        using var project = new SampleProject(environment, "HelloWorld", "HelloWorld.csproj", InaReference.Sdk, properties: string.Empty);

        var publish = await project.PublishAsync();

        // Referring to the SDK changes nothing by itself: this is the framework-dependent output of the .NET SDK.
        Assert.True(publish.ExitCode == 0, publish.Output + publish.Error);
        Assert.DoesNotContain("with Ina", publish.Output);
        Assert.True(File.Exists(Path.Combine(project.PublishDirectory, "HelloWorld.dll")));
        Assert.True(File.Exists(Path.Combine(project.PublishDirectory, "HelloWorld.runtimeconfig.json")));
    }

    private static string[] CommonLines(string output) =>
        output.Split('\n').Where(static line => !line.StartsWith('#')).ToArray();
}
