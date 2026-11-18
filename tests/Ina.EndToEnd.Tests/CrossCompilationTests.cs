using Ina.Linker;

namespace Ina.EndToEnd.Tests;

/// <summary>
/// Building for another processor architecture needs nothing but the runtime pack of the target, which Ina downloads.
/// The executables are run when an AArch64 machine or emulator is available.
/// </summary>
public class CrossCompilationTests
{
    private static readonly TargetPlatform LinuxArm64 = new(TargetOperatingSystem.Linux, TargetArchitecture.Arm64);

    [Fact]
    public async Task HelloWorldIsBuiltForArm64()
    {
        using var directory = new OutputDirectory();

        var build = await TestEnvironment.BuildAsync(
            TestEnvironment.SampleAssembly("HelloWorld", "HelloWorld"), directory.Path, o => o with { Target = LinuxArm64, InvariantGlobalization = true });

        // e_machine of the ELF header: EM_AARCH64.
        var header = new byte[20];
        await using (var stream = File.OpenRead(build.OutputPath))
        {
            await stream.ReadExactlyAsync(header, TestContext.Current.CancellationToken);
        }

        Assert.Equal(183, BitConverter.ToUInt16(header, 18));
        Assert.Contains("ld-linux-aarch64.so.1", build.Link.NeededLibraries);

        Assert.SkipUnless(TestEnvironment.CanRunLinuxArm64, "Running the executable requires an AArch64 Linux host, or qemu-user with an AArch64 C library.");
        var run = await TestEnvironment.RunArm64Async(build.OutputPath, "from", "arm64");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal($"Hello, World!{Environment.NewLine}Arguments: from, arm64{Environment.NewLine}", run.Output);
    }

    [Fact]
    public async Task RuntimeFeaturesWorkOnArm64()
    {
        Assert.SkipUnless(TestEnvironment.CanRunLinuxArm64, "Running the executable requires an AArch64 Linux host, or qemu-user with an AArch64 C library.");
        using var directory = new OutputDirectory();
        var assembly = TestEnvironment.SampleAssembly("RuntimeTour", "RuntimeTour");

        var build = await TestEnvironment.BuildAsync(assembly, directory.Path, static o => o with { Target = LinuxArm64 });
        var run = await TestEnvironment.RunArm64Async(build.OutputPath);

        Assert.True(run.ExitCode == 0, run.Output + run.Error);
        Assert.DoesNotContain("FAIL", run.Output);
        Assert.Contains("All checks passed.", run.Output);
    }

    [Fact]
    public async Task MultiAssemblyJsonApplicationProducesTheSameOutputOnArm64()
    {
        Assert.SkipUnless(TestEnvironment.CanRunLinuxArm64, "Running the executable requires an AArch64 Linux host, or qemu-user with an AArch64 C library.");
        using var directory = new OutputDirectory();
        var assembly = TestEnvironment.SampleAssembly(Path.Combine("JsonReader", "JsonReader"), "JsonReader");
        var data = Path.Combine(Path.GetDirectoryName(assembly)!, "catalog.json");

        var build = await TestEnvironment.BuildAsync(assembly, directory.Path, static o => o with { Target = LinuxArm64 });
        var native = await TestEnvironment.RunArm64Async(build.OutputPath, data);
        var reference = await TestEnvironment.RunOnJitAsync(assembly, data);

        Assert.Equal(0, native.ExitCode);
        Assert.Equal(reference.Output, native.Output);
    }
}
