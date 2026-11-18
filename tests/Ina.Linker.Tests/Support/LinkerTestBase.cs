using System.Diagnostics;
using System.Runtime.InteropServices;
using Ina.Linker.Abi;

namespace Ina.Linker.Tests.Support;

/// <summary>Common plumbing for tests that link objects and optionally run the result.</summary>
public abstract class LinkerTestBase : IDisposable
{
    private const string Arm64Emulator = "qemu-aarch64";
    private const string Arm64SystemRoot = "/usr/aarch64-linux-gnu";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ina-tests", Guid.NewGuid().ToString("N"));
    private int _outputs;

    protected LinkerTestBase()
    {
        Directory.CreateDirectory(_directory);
    }

    /// <summary>Whether binaries produced for x86-64 Linux can be executed on this machine.</summary>
    protected static bool CanRunLinuxX64 => OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    /// <summary>
    /// Whether binaries produced for AArch64 Linux can be executed: natively, or through user mode emulation with
    /// the C library of a cross toolchain (packages qemu-user and libc6-arm64-cross on Debian and Ubuntu).
    /// </summary>
    protected static bool CanRunLinuxArm64 => OperatingSystem.IsLinux()
        && (RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            || (FindOnPath(Arm64Emulator) is not null && File.Exists(Path.Combine(Arm64SystemRoot, "lib", "ld-linux-aarch64.so.1"))));

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

    /// <summary>Links the inputs into an AArch64 executable.</summary>
    internal string LinkArm64(params byte[][] inputs) =>
        Link(static o => o with { Architecture = TargetArchitecture.Arm64, Imports = ImportLibrarySet.ForGlibc(TargetArchitecture.Arm64) }, inputs);

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

    /// <summary>Runs a linked x86-64 executable and returns its exit code and standard output.</summary>
    protected static (int ExitCode, string Output) Run(string path)
    {
        Assert.SkipUnless(CanRunLinuxX64, "Running the linked executable requires an x86-64 Linux host.");
        return Start(new ProcessStartInfo(path));
    }

    /// <summary>Runs a linked AArch64 executable, under emulation when the host is not AArch64.</summary>
    protected static (int ExitCode, string Output) RunArm64(string path)
    {
        Assert.SkipUnless(CanRunLinuxArm64, "Running the linked executable requires an AArch64 Linux host, or qemu-user with an AArch64 C library.");
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            return Start(new ProcessStartInfo(path));
        }

        var startInfo = new ProcessStartInfo(Arm64Emulator);
        startInfo.ArgumentList.Add("-L");
        startInfo.ArgumentList.Add(Arm64SystemRoot);
        startInfo.ArgumentList.Add(path);
        return Start(startInfo);
    }

    private static string? FindOnPath(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, program))
            .FirstOrDefault(File.Exists);

    private static (int ExitCode, string Output) Start(ProcessStartInfo startInfo)
    {
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;

        using var process = Process.Start(startInfo)!;
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
