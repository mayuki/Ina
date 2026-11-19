using Ina.Compilation;
using Ina.Linker;

namespace Ina.EndToEnd.Tests;

/// <summary>
/// Compiles the sample applications for Windows with the full pipeline. Building needs neither Windows nor its
/// SDK, so that part runs on every host; the executables are run when the host is an x64 Windows machine, with
/// the regular .NET runtime executing the same assembly as the reference.
/// </summary>
public class WindowsApplicationTests
{
    private static readonly TargetPlatform WindowsX64 = new(TargetOperatingSystem.Windows, TargetArchitecture.X64);

    private const string CanNotRun = "Running the executable requires an x64 Windows host.";

    [Fact]
    public async Task HelloWorldIsBuiltForWindowsOnAnyHost()
    {
        using var directory = new OutputDirectory();

        var build = await TestEnvironment.BuildAsync(TestEnvironment.SampleAssembly("HelloWorld", "HelloWorld"), directory.Path, WindowsX64);

        // A PE image for x64: "MZ", the PE signature where the DOS header points to, and the machine type.
        var image = await File.ReadAllBytesAsync(build.OutputPath, TestContext.Current.CancellationToken);
        var header = BitConverter.ToInt32(image, 0x3c);
        Assert.Equal("MZ"u8.ToArray(), image[..2]);
        Assert.Equal("PE\0\0"u8.ToArray(), image[header..(header + 4)]);
        Assert.Equal(0x8664, BitConverter.ToUInt16(image, header + 4));
        Assert.EndsWith("HelloWorld.exe", build.OutputPath);
        Assert.True(File.Exists(Path.Combine(directory.Path, "obj", "HelloWorld.obj")));

        Assert.SkipUnless(TestEnvironment.CanRunWindowsX64, CanNotRun);
        var run = await TestEnvironment.RunAsync(build.OutputPath, "first", "second argument");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal($"Hello, World!{Environment.NewLine}Arguments: first, second argument{Environment.NewLine}", run.Output);
        Assert.Empty(run.Error);
    }

    [Theory]
    [InlineData(TargetArchitecture.X64)]
    [InlineData(TargetArchitecture.Arm64)]
    public async Task ExecutableDependsOnlyOnLibrariesThatEveryWindowsHas(TargetArchitecture architecture)
    {
        using var directory = new OutputDirectory();

        var build = await TestEnvironment.BuildAsync(
            TestEnvironment.SampleAssembly("RuntimeTour", "RuntimeTour"), directory.Path, new TargetPlatform(TargetOperatingSystem.Windows, architecture));

        // System DLLs and the Universal C Runtime, which is part of Windows 10 and later. In particular not the
        // redistributable runtime of Visual C++ (vcruntime140.dll), which a machine may not have.
        Assert.NotEmpty(build.Link.NeededLibraries);
        Assert.All(build.Link.NeededLibraries, static library => Assert.True(
            library is "KERNEL32.dll" or "ADVAPI32.dll" or "bcrypt.dll" or "ole32.dll" or "OLEAUT32.dll" or "USER32.dll" or "WS2_32.dll"
                or "ntdll.dll" or "api-ms-win-core-synch-l1-2-0.dll"
            || library.StartsWith("api-ms-win-crt-", StringComparison.Ordinal),
            library));
        Assert.Contains("KERNEL32.dll", build.Link.NeededLibraries);
        Assert.Empty(build.Link.RequiredVersions);
    }

    [Fact]
    public async Task HelloWorldIsCrossCompiledForArm64()
    {
        using var directory = new OutputDirectory();

        var build = await TestEnvironment.BuildAsync(
            TestEnvironment.SampleAssembly("HelloWorld", "HelloWorld"), directory.Path, new TargetPlatform(TargetOperatingSystem.Windows, TargetArchitecture.Arm64));

        // The machine type of the PE header: IMAGE_FILE_MACHINE_ARM64. The executable is not run: that needs
        // Windows on ARM64, and the parts that the linker contributes are covered by the emulator tests of the linker.
        var image = await File.ReadAllBytesAsync(build.OutputPath, TestContext.Current.CancellationToken);
        Assert.Equal(0xaa64, BitConverter.ToUInt16(image, BitConverter.ToInt32(image, 0x3c) + 4));
        Assert.Contains("GetStdHandle", build.Link.ImportedSymbols);
    }

    [Fact]
    public async Task MultiAssemblyJsonApplicationBehavesLikeOnTheRegularRuntime()
    {
        Assert.SkipUnless(TestEnvironment.CanRunWindowsX64, CanNotRun);
        using var directory = new OutputDirectory();
        var assembly = TestEnvironment.SampleAssembly(Path.Combine("JsonReader", "JsonReader"), "JsonReader");
        var data = Path.Combine(Path.GetDirectoryName(assembly)!, "catalog.json");

        var build = await TestEnvironment.BuildAsync(assembly, directory.Path, WindowsX64);
        var native = await TestEnvironment.RunAsync(build.OutputPath, data);
        var reference = await TestEnvironment.RunOnJitAsync(assembly, data);

        Assert.Equal(0, native.ExitCode);
        Assert.Contains("Most expensive: Mechanical Keyboard", native.Output);
        Assert.Equal(reference.Output, native.Output);

        // Failures surface the same way: a message on standard error and a non-zero exit code.
        var broken = Path.Combine(directory.Path, "broken.json");
        await File.WriteAllTextAsync(broken, "{ \"name\": ", TestContext.Current.CancellationToken);
        var invalid = await TestEnvironment.RunAsync(build.OutputPath, broken);
        Assert.Equal(2, invalid.ExitCode);
        Assert.StartsWith("Invalid JSON:", invalid.Error);
    }

    [Fact]
    public async Task RuntimeFeaturesWorkInTheNativeExecutable()
    {
        Assert.SkipUnless(TestEnvironment.CanRunWindowsX64, CanNotRun);
        using var directory = new OutputDirectory();
        var assembly = TestEnvironment.SampleAssembly("RuntimeTour", "RuntimeTour");

        var build = await TestEnvironment.BuildAsync(assembly, directory.Path, WindowsX64);
        var native = await TestEnvironment.RunAsync(build.OutputPath);
        var reference = await TestEnvironment.RunOnJitAsync(assembly);

        // Exceptions, garbage collection, threads and thread statics: this exercises the unwind tables, the
        // thread-local storage and the import tables that the linker produced.
        Assert.True(native.ExitCode == 0, native.Output + native.Error);
        Assert.DoesNotContain("FAIL", native.Output);
        Assert.Contains("All checks passed.", native.Output);

        // Lines starting with '#' describe the execution mode; everything else must be identical.
        Assert.Equal(CommonLines(reference.Output), CommonLines(native.Output));
        Assert.Contains("# dynamic code: False", native.Output);
    }

    [Theory]
    [InlineData(OptimizationMode.None, true)]
    [InlineData(OptimizationMode.Size, true)]
    [InlineData(OptimizationMode.Speed, true)]
    [InlineData(OptimizationMode.Blended, false)]
    public async Task BuildOptionsProduceWorkingExecutables(OptimizationMode optimization, bool relocatable)
    {
        Assert.SkipUnless(TestEnvironment.CanRunWindowsX64, CanNotRun);
        using var directory = new OutputDirectory();

        var build = await TestEnvironment.BuildAsync(
            TestEnvironment.SampleAssembly("RuntimeTour", "RuntimeTour"),
            directory.Path,
            WindowsX64,
            o => o with { Optimization = optimization, PositionIndependent = relocatable, InvariantGlobalization = true });
        var run = await TestEnvironment.RunAsync(build.OutputPath);

        Assert.True(run.ExitCode == 0, run.Output + run.Error);
        Assert.Contains("All checks passed.", run.Output);
    }

    [Fact]
    public async Task InaCompilesItselfForWindowsAndTheNativeToolBuildsAWorkingApplication()
    {
        Assert.SkipUnless(TestEnvironment.CanRunWindowsX64, CanNotRun);
        using var directory = new OutputDirectory();
        var hello = Path.Combine(directory.Path, "hello.exe");

        var native = await TestEnvironment.BuildAsync(TestEnvironment.InaAssembly, Path.Combine(directory.Path, "tool"), WindowsX64);
        var build = await TestEnvironment.RunAsync(
            native.OutputPath, TestEnvironment.SampleAssembly("HelloWorld", "HelloWorld"), "--os", "windows", "--arch", "x64", "-o", hello,
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
