using System.Reflection;

[assembly: AssemblyFixture(typeof(Ina.EndToEnd.Tests.PublishEnvironment))]

namespace Ina.EndToEnd.Tests;

/// <summary>
/// What the tests of the <c>dotnet publish</c> integration share: the Ina.Sdk package of this build in a local
/// package feed, and an environment for the .NET CLI in which no native toolchain can be found.
/// </summary>
public sealed class PublishEnvironment : IAsyncLifetime
{
    private readonly OutputDirectory _directory = new();
    private string? _extractedPackage;

    /// <summary>The folder with the Ina.Sdk package; the build of the test project fills it.</summary>
    public string Feed { get; } = Path.Combine(AppContext.BaseDirectory, "feed");

    /// <summary>The version of the package in <see cref="Feed"/>. Its label is reserved for these tests.</summary>
    public string SdkVersion { get; } = typeof(PublishEnvironment).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(static a => a.Key == "InaSdkVersion").Value!;

    /// <summary>
    /// Environment variables for the .NET CLI. On Unix the search path contains nothing but <c>dotnet</c> and the
    /// shell that MSBuild starts programs with, so a publish that looks for clang, gcc, ld or objcopy fails.
    /// </summary>
    public Dictionary<string, string?> CommandLineEnvironment { get; } = new()
    {
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["DOTNET_CLI_UI_LANGUAGE"] = "en",

        // Build nodes that outlive a command would keep the task assembly of an earlier build loaded.
        ["MSBUILDDISABLENODEREUSE"] = "1",

        // The samples treat warnings as errors, and NuGet warns when an advisory exists for the compiler package of
        // the installed .NET SDK. That says something about the SDK of the machine, not about what is tested here.
        ["NuGetAudit"] = "false",
    };

    public async ValueTask InitializeAsync()
    {
        Assert.True(
            File.Exists(Path.Combine(Feed, $"Ina.Sdk.{SdkVersion}.nupkg")),
            $"The Ina.Sdk package was not built into {Feed}.");

        // NuGet never replaces a package it has already extracted, and the version of the test package stays the
        // same from build to build. Removing the extracted copy makes every run use the package it just built.
        const string marker = "global-packages:";
        var locals = await TestEnvironment.RunAsync("dotnet", "nuget", "locals", "global-packages", "--list", "--force-english-output");
        var index = locals.Output.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(locals.ExitCode == 0 && index >= 0, locals.Output + locals.Error);
        _extractedPackage = Path.Combine(locals.Output[(index + marker.Length)..].Trim(), "ina.sdk", SdkVersion.ToLowerInvariant());
        RemoveExtractedPackage();

        if (!OperatingSystem.IsWindows())
        {
            var tools = Directory.CreateDirectory(Path.Combine(_directory.Path, "bin")).FullName;
            foreach (var tool in new[] { "dotnet", "sh" })
            {
                File.CreateSymbolicLink(Path.Combine(tools, tool), FindOnPath(tool));
            }

            CommandLineEnvironment["PATH"] = tools;
        }
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            // The test package is of no use to anything else on the machine.
            RemoveExtractedPackage();
        }
        catch (IOException)
        {
            // Best effort; the next run removes what stays behind before it restores anything.
        }

        _directory.Dispose();
        return ValueTask.CompletedTask;
    }

    private void RemoveExtractedPackage()
    {
        if (_extractedPackage is not null && Directory.Exists(_extractedPackage))
        {
            Directory.Delete(_extractedPackage, recursive: true);
        }
    }

    private static string FindOnPath(string program)
    {
        var path = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, program))
            .FirstOrDefault(File.Exists);
        Assert.True(path is not null, $"{program} was not found on the PATH.");
        return File.ResolveLinkTarget(path!, returnFinalTarget: true)?.FullName ?? path!;
    }
}
