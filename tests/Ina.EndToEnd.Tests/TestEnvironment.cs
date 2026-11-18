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

    /// <summary>Path of the IL assembly of a sample, as built by <c>dotnet build</c>.</summary>
    public static string SampleAssembly(string projectDirectory, string assemblyName)
    {
        var path = Path.GetFullPath(Path.Combine(
            Metadata["SamplesDirectory"], projectDirectory, "bin", Metadata["SamplesConfiguration"], "net10.0", assemblyName + ".dll"));
        Assert.True(File.Exists(path), $"The sample was not built: {path}");
        return path;
    }

    /// <summary>Compiles an application to a native executable inside <paramref name="directory"/>.</summary>
    public static async Task<BuildResult> BuildAsync(string assembly, string directory, Func<BuildOptions, BuildOptions>? configure = null)
    {
        var options = new BuildOptions
        {
            AssemblyPath = assembly,
            OutputPath = Path.Combine(directory, Path.GetFileNameWithoutExtension(assembly)),
            Target = TargetPlatform.Parse("linux", "x64"),
            IntermediateDirectory = Path.Combine(directory, "obj"),
        };

        using var packages = PackageStore.CreateDefault(NullLogger.Instance);
        return await new BuildPipeline(packages, NullLogger.Instance).BuildAsync(configure?.Invoke(options) ?? options, TestContext.Current.CancellationToken);
    }

    /// <summary>Runs the IL assembly on the regular .NET runtime, which serves as the reference behavior.</summary>
    public static Task<ProcessResult> RunOnJitAsync(string assembly, params string[] arguments) => RunAsync("dotnet", ["exec", assembly, .. arguments]);

    public static async Task<ProcessResult> RunAsync(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
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
