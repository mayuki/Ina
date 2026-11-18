using System.Diagnostics;
using System.Runtime.InteropServices;
using Ina.Linker.Abi;

namespace Ina.Linker.Tests.Support;

/// <summary>Common plumbing for tests that link objects and optionally run the result.</summary>
public abstract class LinkerTestBase : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ina-tests", Guid.NewGuid().ToString("N"));
    private int _outputs;

    protected LinkerTestBase()
    {
        Directory.CreateDirectory(_directory);
    }

    /// <summary>Whether binaries produced for x86-64 Linux can be executed on this machine.</summary>
    protected static bool CanRunLinuxX64 => OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of temporary files.
        }

        GC.SuppressFinalize(this);
    }

    internal string Link(params byte[][] inputs) => Link(static o => o, inputs);

    /// <summary>Links the inputs with default options adjusted by <paramref name="configure"/> and returns the output path.</summary>
    internal string Link(Func<LinkOptions, LinkOptions> configure, params byte[][] inputs)
    {
        var output = Path.Combine(_directory, $"out{_outputs++}");
        var options = new LinkOptions
        {
            OutputPath = output,
            Architecture = TargetArchitecture.X64,
            Imports = ImportLibrarySet.ForGlibc(TargetArchitecture.X64),
            Inputs = inputs.Select(static (data, i) => new LinkInput($"input{i}", data)).ToList(),
            EmitSymbolTable = true,
        };
        ElfLinker.Link(configure(options));
        return output;
    }

    internal static ElfImage Load(string path) => new(File.ReadAllBytes(path));

    /// <summary>Runs a linked executable and returns its exit code and standard output.</summary>
    protected static (int ExitCode, string Output) Run(string path)
    {
        Assert.SkipUnless(CanRunLinuxX64, "Running the linked executable requires an x86-64 Linux host.");

        using var process = Process.Start(new ProcessStartInfo(path) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            process.Kill();
            Assert.Fail("The linked executable did not exit in time.");
        }

        if (error.Length > 0)
        {
            output += error;
        }

        return (process.ExitCode, output);
    }
}
