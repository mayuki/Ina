using Ina.Compilation;

namespace Ina.EndToEnd.Tests;

/// <summary>
/// Compiles the sample applications with the full pipeline and runs the resulting executables. The regular .NET
/// runtime executing the same assembly is the reference for the expected behavior.
/// </summary>
public class SampleApplicationTests
{
    [Fact]
    public async Task HelloWorldPrintsItsGreetingAndReceivesArguments()
    {
        Assert.SkipUnless(TestEnvironment.CanRunLinuxX64, "Running the executable requires an x86-64 Linux host.");
        using var directory = new OutputDirectory();

        var build = await TestEnvironment.BuildAsync(TestEnvironment.SampleAssembly("HelloWorld", "HelloWorld"), directory.Path);
        var run = await TestEnvironment.RunAsync(build.OutputPath, "first", "second argument");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal($"Hello, World!{Environment.NewLine}Arguments: first, second argument{Environment.NewLine}", run.Output);
        Assert.Empty(run.Error);
    }

    [Fact]
    public async Task MultiAssemblyJsonApplicationBehavesLikeOnTheRegularRuntime()
    {
        Assert.SkipUnless(TestEnvironment.CanRunLinuxX64, "Running the executable requires an x86-64 Linux host.");
        using var directory = new OutputDirectory();
        var assembly = TestEnvironment.SampleAssembly(Path.Combine("JsonReader", "JsonReader"), "JsonReader");
        var data = Path.Combine(Path.GetDirectoryName(assembly)!, "catalog.json");

        var build = await TestEnvironment.BuildAsync(assembly, directory.Path);
        var native = await TestEnvironment.RunAsync(build.OutputPath, data);
        var reference = await TestEnvironment.RunOnJitAsync(assembly, data);

        Assert.Equal(0, native.ExitCode);
        Assert.Contains("Most expensive: Mechanical Keyboard", native.Output);
        Assert.Contains("抹茶ラテ", native.Output);
        Assert.Equal(reference.Output, native.Output);

        // Failures surface the same way: a message on standard error and a non-zero exit code.
        var broken = Path.Combine(directory.Path, "broken.json");
        await File.WriteAllTextAsync(broken, "{ \"name\": ", TestContext.Current.CancellationToken);
        var invalid = await TestEnvironment.RunAsync(build.OutputPath, broken);
        Assert.Equal(2, invalid.ExitCode);
        Assert.StartsWith("Invalid JSON:", invalid.Error);

        var missing = await TestEnvironment.RunAsync(build.OutputPath, Path.Combine(directory.Path, "missing.json"));
        Assert.Equal(1, missing.ExitCode);
    }

    [Fact]
    public async Task RuntimeFeaturesWorkInTheNativeExecutable()
    {
        Assert.SkipUnless(TestEnvironment.CanRunLinuxX64, "Running the executable requires an x86-64 Linux host.");
        using var directory = new OutputDirectory();
        var assembly = TestEnvironment.SampleAssembly("RuntimeTour", "RuntimeTour");

        var build = await TestEnvironment.BuildAsync(assembly, directory.Path);
        var native = await TestEnvironment.RunAsync(build.OutputPath);
        var reference = await TestEnvironment.RunOnJitAsync(assembly);

        Assert.True(native.ExitCode == 0, native.Output + native.Error);
        Assert.DoesNotContain("FAIL", native.Output);
        Assert.Contains("All checks passed.", native.Output);

        // Lines starting with '#' describe the execution mode; everything else must be identical.
        Assert.Equal(CommonLines(reference.Output), CommonLines(native.Output));
        Assert.Contains("# dynamic code: False", native.Output);
        Assert.Contains("# dynamic code: True", reference.Output);
    }

    [Theory]
    [InlineData(OptimizationMode.None, true, false)]
    [InlineData(OptimizationMode.Size, true, false)]
    [InlineData(OptimizationMode.Speed, true, true)]
    [InlineData(OptimizationMode.Blended, false, true)]
    public async Task BuildOptionsProduceWorkingExecutables(OptimizationMode optimization, bool positionIndependent, bool symbols)
    {
        Assert.SkipUnless(TestEnvironment.CanRunLinuxX64, "Running the executable requires an x86-64 Linux host.");
        using var directory = new OutputDirectory();

        var build = await TestEnvironment.BuildAsync(
            TestEnvironment.SampleAssembly("HelloWorld", "HelloWorld"),
            directory.Path,
            o => o with { Optimization = optimization, PositionIndependent = positionIndependent, EmitSymbolTable = symbols, InvariantGlobalization = true });
        var run = await TestEnvironment.RunAsync(build.OutputPath);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal($"Hello, World!{Environment.NewLine}", run.Output);
    }

    [Fact]
    public async Task ExecutableDependsOnlyOnTheBaselineCLibrary()
    {
        using var directory = new OutputDirectory();

        var build = await TestEnvironment.BuildAsync(TestEnvironment.SampleAssembly("RuntimeTour", "RuntimeTour"), directory.Path);

        // No C++ runtime, no compiler support library: only the C library and its companions.
        string[] allowed = ["libdl.so.2", "librt.so.1", "libm.so.6", "libpthread.so.0", "libc.so.6", "ld-linux-x86-64.so.2"];
        Assert.All(build.Link.NeededLibraries, library => Assert.Contains(library, allowed));
        Assert.Contains("libc.so.6", build.Link.NeededLibraries);

        // Everything binds to symbol versions that exist in the oldest C library .NET supports (glibc 2.27).
        Assert.NotEmpty(build.Link.RequiredVersions);
        Assert.All(build.Link.RequiredVersions, static requirement =>
        {
            var version = Version.Parse(requirement[(requirement.LastIndexOf('_') + 1)..]);
            Assert.True(version <= new Version(2, 27), requirement);
        });
    }

    [Fact]
    public async Task IntermediateFilesAreWrittenToTheRequestedDirectory()
    {
        using var directory = new OutputDirectory();

        var build = await TestEnvironment.BuildAsync(TestEnvironment.SampleAssembly("HelloWorld", "HelloWorld"), directory.Path);

        Assert.True(File.Exists(Path.Combine(directory.Path, "obj", "HelloWorld.o")));
        Assert.True(File.Exists(Path.Combine(directory.Path, "obj", "HelloWorld.ilc.rsp")));
        Assert.Equal(new FileInfo(build.OutputPath).Length, build.OutputSize);
        Assert.StartsWith("10.0.", build.RuntimeVersion);
    }

    [Fact]
    public async Task PathsWithSpacesAreSupported()
    {
        Assert.SkipUnless(TestEnvironment.CanRunLinuxX64, "Running the executable requires an x86-64 Linux host.");
        using var directory = new OutputDirectory();
        var spaced = Path.Combine(directory.Path, "my app");
        Directory.CreateDirectory(spaced);
        var original = TestEnvironment.SampleAssembly("HelloWorld", "HelloWorld");
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(original)!, "HelloWorld.*"))
        {
            File.Copy(file, Path.Combine(spaced, Path.GetFileName(file)));
        }

        var build = await TestEnvironment.BuildAsync(Path.Combine(spaced, "HelloWorld.dll"), Path.Combine(spaced, "out put"));
        var run = await TestEnvironment.RunAsync(build.OutputPath);

        Assert.Equal($"Hello, World!{Environment.NewLine}", run.Output);
    }

    [Fact]
    public async Task ClassLibraryIsRejectedBecauseItHasNoEntryPoint()
    {
        using var directory = new OutputDirectory();
        var library = TestEnvironment.SampleAssembly(Path.Combine("JsonReader", "JsonReader"), "JsonReader.Models");

        var error = await Assert.ThrowsAsync<InaException>(() => TestEnvironment.BuildAsync(library, directory.Path));

        Assert.Contains("no entry point", error.Message);
    }

    [Fact]
    public async Task OutputPathThatIsADirectoryIsRejected()
    {
        using var directory = new OutputDirectory();

        var error = await Assert.ThrowsAsync<InaException>(() => TestEnvironment.BuildAsync(
            TestEnvironment.SampleAssembly("HelloWorld", "HelloWorld"), directory.Path, o => o with { OutputPath = directory.Path }));

        Assert.Contains("is a directory", error.Message);
    }

    private static string[] CommonLines(string output) =>
        output.Split('\n').Where(static line => !line.StartsWith('#')).ToArray();
}
