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

    /// <summary>
    /// Whether an x86-64 Linux is at hand on a Windows machine: the Windows Subsystem for Linux with a default
    /// distribution. Probed once, by asking it for its architecture.
    /// </summary>
    private static readonly Lazy<bool> WindowsSubsystemForLinux = new(static () =>
    {
        if (!OperatingSystem.IsWindows() || FindOnPath("wsl.exe") is null)
        {
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo("wsl.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            startInfo.ArgumentList.Add("--exec");
            startInfo.ArgumentList.Add("uname");
            startInfo.ArgumentList.Add("-m");
            using var process = Process.Start(startInfo)!;
            var machine = process.StandardOutput.ReadToEnd();
            return process.WaitForExit(TimeSpan.FromSeconds(60)) && process.ExitCode == 0 && machine.Trim() == "x86_64";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    });

    /// <summary>
    /// Whether binaries produced for x86-64 Linux can be executed on this machine: natively, or on Windows through
    /// the Windows Subsystem for Linux.
    /// </summary>
    protected static bool CanRunLinuxX64 =>
        (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64) || WindowsSubsystemForLinux.Value;

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
        Assert.SkipUnless(CanRunLinuxX64, "Running the linked executable requires an x86-64 Linux host, or Windows with WSL.");
        if (!OperatingSystem.IsWindows())
        {
            return Start(new ProcessStartInfo(path));
        }

        // The Linux side sees the drives of Windows under /mnt.
        var full = Path.GetFullPath(path);
        var startInfo = new ProcessStartInfo("wsl.exe");
        startInfo.ArgumentList.Add("--exec");
        startInfo.ArgumentList.Add($"/mnt/{char.ToLowerInvariant(full[0])}{full[2..].Replace('\\', '/')}");
        return Start(startInfo);
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

    /// <summary>
    /// Starts a process, retrying while the executable is "busy". Tests run in parallel, and a process forked by
    /// another test briefly inherits the handle through which this test wrote its executable; the kernel refuses to
    /// execute a file that is open for writing anywhere (ETXTBSY).
    /// </summary>
    private static Process StartWithRetry(ProcessStartInfo startInfo)
    {
        const int TextFileBusy = 26;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return Process.Start(startInfo)!;
            }
            catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == TextFileBusy && attempt < 50)
            {
                Thread.Sleep(20);
            }
        }
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

        using var process = StartWithRetry(startInfo);
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
