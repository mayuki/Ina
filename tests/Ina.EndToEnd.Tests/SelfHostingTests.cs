namespace Ina.EndToEnd.Tests;

/// <summary>
/// Ina is itself a .NET application, so it can compile itself. The resulting native tool is a much larger program
/// than the samples (command line parsing, HTTP, zip, JSON, metadata reading, the linker) and has to do real work.
/// </summary>
public class SelfHostingTests
{
    [Fact]
    public async Task InaCompilesItselfAndTheNativeToolBuildsAWorkingApplication()
    {
        Assert.SkipUnless(TestEnvironment.CanRunLinuxX64, "Running the executable requires an x86-64 Linux host.");
        using var directory = new OutputDirectory();
        var hello = Path.Combine(directory.Path, "hello");

        var native = await TestEnvironment.BuildAsync(TestEnvironment.InaAssembly, Path.Combine(directory.Path, "tool"));
        var build = await TestEnvironment.RunAsync(
            native.OutputPath, TestEnvironment.SampleAssembly("HelloWorld", "HelloWorld"), "--arch", "x64", "-o", hello,
            "--intermediate-dir", Path.Combine(directory.Path, "obj"));

        Assert.True(build.ExitCode == 0, build.Output + build.Error);
        Assert.Contains($"HelloWorld.dll -> {hello}", build.Output);

        var run = await TestEnvironment.RunAsync(hello, "self", "hosted");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal($"Hello, World!{Environment.NewLine}Arguments: self, hosted{Environment.NewLine}", run.Output);
    }

    [Fact]
    public async Task NativeToolReportsErrorsLikeTheManagedOne()
    {
        Assert.SkipUnless(TestEnvironment.CanRunLinuxX64, "Running the executable requires an x86-64 Linux host.");
        using var directory = new OutputDirectory();

        var native = await TestEnvironment.BuildAsync(TestEnvironment.InaAssembly, Path.Combine(directory.Path, "tool"));
        var help = await TestEnvironment.RunAsync(native.OutputPath, "--help");
        var missing = await TestEnvironment.RunAsync(native.OutputPath, "does-not-exist.dll");

        Assert.Equal(0, help.ExitCode);
        Assert.Contains("--arch", help.Output);
        Assert.Equal(1, missing.ExitCode);
        Assert.Contains("error: Assembly not found", missing.Error);
    }
}
