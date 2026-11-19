using System.Diagnostics;
using System.Runtime.InteropServices;
using Ina.Linker.Abi;

namespace Ina.Linker.Tests.Support;

/// <summary>Common plumbing for tests that link COFF objects into Windows executables and optionally run the result.</summary>
public abstract class PeLinkerTestBase : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ina-tests", Guid.NewGuid().ToString("N"));
    private int _outputs;

    protected PeLinkerTestBase()
    {
        Directory.CreateDirectory(_directory);
    }

    /// <summary>Whether executables produced for x64 Windows can be run on this machine.</summary>
    protected static bool CanRunWindowsX64 => OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    /// <summary>Whether executables produced for ARM64 Windows can be run on this machine.</summary>
    protected static bool CanRunWindowsArm64 => OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort cleanup of temporary files; a virus scanner may still hold an executable.
        }

        GC.SuppressFinalize(this);
    }

    internal string Link(params byte[][] inputs) => Link(static o => o, inputs);

    /// <summary>Links the inputs with default options adjusted by <paramref name="configure"/> and returns the output path.</summary>
    internal string Link(Func<PeLinkOptions, PeLinkOptions> configure, params byte[][] inputs)
    {
        var output = Path.Combine(_directory, $"out{_outputs++}.exe");
        var options = new PeLinkOptions
        {
            OutputPath = output,
            Architecture = TargetArchitecture.X64,
            Imports = ImportLibrarySet.ForWindows(),
            Inputs = inputs.Select(static (data, i) => new LinkInput($"input{i}", data)).ToList(),
        };
        return PeLinker.Link(configure(options)).OutputPath;
    }

    /// <summary>Links the inputs into an executable for ARM64.</summary>
    internal string LinkArm64(params byte[][] inputs) => Link(static o => o with { Architecture = TargetArchitecture.Arm64 }, inputs);

    internal static PeImage Load(string path) => new(File.ReadAllBytes(path));

    /// <summary>Runs a linked x64 executable and returns its exit code and what it wrote to standard output and error.</summary>
    protected static (int ExitCode, string Output) Run(string path)
    {
        Assert.SkipUnless(CanRunWindowsX64, "Running the linked executable requires an x64 Windows host.");
        return Start(path);
    }

    /// <summary>Runs a linked ARM64 executable.</summary>
    protected static (int ExitCode, string Output) RunArm64(string path)
    {
        Assert.SkipUnless(CanRunWindowsArm64, "Running the linked executable requires a Windows on ARM64 host.");
        return Start(path);
    }

    private static (int ExitCode, string Output) Start(string path)
    {
        using var process = Process.Start(new ProcessStartInfo(path) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            process.Kill();
            Assert.Fail("The linked executable did not exit in time.");
        }

        return (process.ExitCode, output + error);
    }
}
