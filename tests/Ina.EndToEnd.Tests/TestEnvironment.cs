using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Ina.Packs;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ina.EndToEnd.Tests;

/// <summary>Locates the sample applications and runs processes for the end-to-end tests.</summary>
internal static class TestEnvironment
{
    private static readonly Dictionary<string, string> Metadata = typeof(TestEnvironment).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .ToDictionary(static a => a.Key, static a => a.Value ?? string.Empty);

    /// <summary>Whether executables built for x86-64 Linux can be run on this machine.</summary>
    public static bool CanRunLinuxX64 => OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    /// <summary>Whether executables built for x64 Windows can be run on this machine.</summary>
    public static bool CanRunWindowsX64 => OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    /// <summary>Whether executables built for ARM64 Windows can be run on this machine.</summary>
    public static bool CanRunWindowsArm64 => OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    private const string Arm64Emulator = "qemu-aarch64";
    private const string Arm64SystemRoot = "/usr/aarch64-linux-gnu";

    /// <summary>
    /// Whether executables built for AArch64 Linux can be run: natively, or through user mode emulation with the
    /// C library of a cross toolchain (packages qemu-user and libc6-arm64-cross on Debian and Ubuntu).
    /// </summary>
    public static bool CanRunLinuxArm64 => OperatingSystem.IsLinux()
        && (RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            || (IsOnPath(Arm64Emulator) && File.Exists(Path.Combine(Arm64SystemRoot, "lib", "ld-linux-aarch64.so.1"))));

    /// <summary>Runs an AArch64 executable, under emulation when the host is not AArch64.</summary>
    public static Task<ProcessResult> RunArm64Async(string path, params string[] arguments) =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? RunAsync(path, arguments)
            : RunAsync(Arm64Emulator, ["-L", Arm64SystemRoot, path, .. arguments]);

    private static bool IsOnPath(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => File.Exists(Path.Combine(directory, program)));

    /// <summary>Path of the IL assembly of a sample, as built by <c>dotnet build</c>.</summary>
    public static string SampleAssembly(string projectDirectory, string assemblyName)
    {
        var path = Path.GetFullPath(Path.Combine(
            Metadata["SamplesDirectory"], projectDirectory, "bin", Metadata["SamplesConfiguration"], Metadata["SamplesTargetFramework"], assemblyName + ".dll"));
        Assert.True(File.Exists(path), $"The sample was not built: {path}");
        return path;
    }

    /// <summary>The IL assembly of the <c>ina</c> tool in its own build output (without the test assemblies around it).</summary>
    public static string InaAssembly
    {
        get
        {
            var path = Path.GetFullPath(Metadata["InaAssembly"]);
            Assert.True(File.Exists(path), $"The ina tool was not built: {path}");
            return path;
        }
    }

    /// <summary>Compiles an application to a native executable for x64 Linux inside <paramref name="directory"/>.</summary>
    public static Task<BuildResult> BuildAsync(string assembly, string directory, Func<BuildOptions, BuildOptions>? configure = null) =>
        BuildAsync(assembly, directory, TargetPlatform.Parse("linux", "x64"), configure);

    /// <summary>Compiles an application to a native executable for <paramref name="target"/> inside <paramref name="directory"/>.</summary>
    public static async Task<BuildResult> BuildAsync(string assembly, string directory, TargetPlatform target, Func<BuildOptions, BuildOptions>? configure = null)
    {
        var options = new BuildOptions
        {
            AssemblyPath = assembly,
            OutputPath = Path.Combine(directory, Path.GetFileNameWithoutExtension(assembly) + target.ExecutableExtension),
            Target = target,
            IntermediateDirectory = Path.Combine(directory, "obj"),
        };

        using var packages = PackageStore.CreateDefault(NullLogger.Instance);
        return await new BuildPipeline(packages, NullLogger.Instance).BuildAsync(configure?.Invoke(options) ?? options, TestContext.Current.CancellationToken);
    }

    /// <summary>Runs the IL assembly on the regular .NET runtime, which serves as the reference behavior.</summary>
    public static Task<ProcessResult> RunOnJitAsync(string assembly, params string[] arguments) => RunAsync("dotnet", ["exec", assembly, .. arguments]);

    /// <summary>
    /// Starts a process, retrying while the executable is "busy". Tests run in parallel, and a process forked by
    /// another test briefly inherits the handle through which this test wrote its executable; the kernel refuses to
    /// execute a file that is open for writing anywhere (ETXTBSY).
    /// </summary>
    private static async Task<Process> StartWithRetryAsync(ProcessStartInfo startInfo)
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
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
        }
    }

    public static Task<ProcessResult> RunAsync(string fileName, params string[] arguments) => RunAsync(fileName, environment: null, arguments);

    /// <summary>Runs a program with additional or replaced environment variables.</summary>
    public static Task<ProcessResult> RunAsync(string fileName, IReadOnlyDictionary<string, string?>? environment, params string[] arguments) =>
        StartAndWaitAsync(string.Empty, fileName, environment, arguments);

    /// <summary>Runs a process with <paramref name="workingDirectory"/> as its current directory.</summary>
    public static Task<ProcessResult> RunInDirectoryAsync(string workingDirectory, string fileName, params string[] arguments) =>
        StartAndWaitAsync(workingDirectory, fileName, environment: null, arguments);

    private static async Task<ProcessResult> StartAndWaitAsync(
        string workingDirectory, string fileName, IReadOnlyDictionary<string, string?>? environment, string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment ?? new Dictionary<string, string?>())
        {
            startInfo.Environment[name] = value;
        }

        using var process = await StartWithRetryAsync(startInfo);
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return new ProcessResult(process.ExitCode, await output, await error);
    }
}

internal sealed record ProcessResult(int ExitCode, string Output, string Error);

/// <summary>A scratch directory for build outputs that is removed after the test.</summary>
internal sealed class OutputDirectory : IDisposable
{
    public OutputDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ina-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of temporary files.
        }
    }
}
