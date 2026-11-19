namespace Ina.EndToEnd.Tests;

/// <summary>Drives the <c>ina</c> command the way a user would.</summary>
public class CommandLineTests
{
    /// <summary>The command line tool is built next to the tests through a project reference.</summary>
    private static string InaAssembly => Path.Combine(AppContext.BaseDirectory, "ina.dll");

    [Fact]
    public async Task HelpDescribesTheOptions()
    {
        var result = await TestEnvironment.RunAsync("dotnet", "exec", InaAssembly, "--help");

        Assert.Equal(0, result.ExitCode);
        foreach (var expected in new[] { "<assembly>", "--output", "--os", "--arch", "--optimize", "--runtime-version", "--invariant-globalization" })
        {
            Assert.Contains(expected, result.Output);
        }
    }

    [Fact]
    public async Task CompilesAnApplicationAndReportsTheResult()
    {
        using var directory = new OutputDirectory();
        var output = Path.Combine(directory.Path, "hello");

        var result = await TestEnvironment.RunAsync(
            "dotnet", "exec", InaAssembly, TestEnvironment.SampleAssembly("HelloWorld", "HelloWorld"), "--os", "linux", "--arch", "x64", "-o", output,
            "--intermediate-dir", Path.Combine(directory.Path, "obj"));

        Assert.True(result.ExitCode == 0, result.Output + result.Error);
        Assert.Contains($"HelloWorld.dll -> {output}", result.Output);
        Assert.Contains("linux-x64", result.Output);
        Assert.True(File.Exists(output));

        if (TestEnvironment.CanRunLinuxX64)
        {
            Assert.Equal($"Hello, World!{Environment.NewLine}", (await TestEnvironment.RunAsync(output)).Output);
        }
    }

    [Fact]
    public async Task CompilesAnApplicationForWindows()
    {
        using var directory = new OutputDirectory();
        var output = Path.Combine(directory.Path, "hello.exe");

        var result = await TestEnvironment.RunAsync(
            "dotnet", "exec", InaAssembly, TestEnvironment.SampleAssembly("HelloWorld", "HelloWorld"), "--os", "windows", "--arch", "x64", "-o", output,
            "--intermediate-dir", Path.Combine(directory.Path, "obj"));

        Assert.True(result.ExitCode == 0, result.Output + result.Error);
        Assert.Contains($"HelloWorld.dll -> {output}", result.Output);
        Assert.Contains("win-x64", result.Output);

        // Windows has no versioned C library, so the summary does not name one.
        Assert.DoesNotContain("glibc", result.Output);
        Assert.True(File.Exists(output));

        if (TestEnvironment.CanRunWindowsX64)
        {
            Assert.Equal($"Hello, World!{Environment.NewLine}", (await TestEnvironment.RunAsync(output)).Output);
        }
    }

    [Fact]
    public async Task OutputIsNamedAfterTheAssemblyWithTheExtensionOfTheTarget()
    {
        using var directory = new OutputDirectory();
        var assembly = TestEnvironment.SampleAssembly("HelloWorld", "HelloWorld");

        // The default output is written to the current directory, so the tool is started there.
        async Task<ProcessResult> BuildAsync(string os) => await TestEnvironment.RunInDirectoryAsync(
            directory.Path, "dotnet", "exec", InaAssembly, assembly, "--os", os, "--arch", "x64", "--intermediate-dir", Path.Combine(directory.Path, "obj-" + os));

        var windows = await BuildAsync("windows");
        var linux = await BuildAsync("linux");

        Assert.True(windows.ExitCode == 0, windows.Output + windows.Error);
        Assert.True(linux.ExitCode == 0, linux.Output + linux.Error);
        Assert.True(File.Exists(Path.Combine(directory.Path, "HelloWorld.exe")));
        Assert.True(File.Exists(Path.Combine(directory.Path, "HelloWorld")));
    }

    [Fact]
    public async Task VerboseModeShowsThePhasesOfTheBuild()
    {
        using var directory = new OutputDirectory();

        var result = await TestEnvironment.RunAsync(
            "dotnet", "exec", InaAssembly, TestEnvironment.SampleAssembly("HelloWorld", "HelloWorld"), "-o", Path.Combine(directory.Path, "hello"),
            "--arch", "x64", "--intermediate-dir", Path.Combine(directory.Path, "obj"), "--verbose");

        Assert.True(result.ExitCode == 0, result.Output + result.Error);
        foreach (var phase in new[] { "ina.resolve-packs took", "ina.compile took", "ina.link took", "Dependencies:" })
        {
            Assert.Contains(phase, result.Output);
        }
    }

    [Fact]
    public async Task MissingAssemblyIsAnErrorWithExitCodeOne()
    {
        var result = await TestEnvironment.RunAsync("dotnet", "exec", InaAssembly, "does-not-exist.dll");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("error: Assembly not found", result.Error);
    }

    [Fact]
    public async Task UnsupportedTargetIsRejectedBeforeAnythingIsBuilt()
    {
        var result = await TestEnvironment.RunAsync(
            "dotnet", "exec", InaAssembly, TestEnvironment.SampleAssembly("HelloWorld", "HelloWorld"), "--os", "plan9");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("plan9", result.Error);
    }
}
