using Microsoft.Extensions.Logging;

namespace Ina.Packs;

/// <summary>The packages a build needs: the compiler for the host and the runtime libraries for the target.</summary>
public sealed record RuntimePacks(string Version, string CompilerDirectory, string RuntimeDirectory, TargetPlatform Target)
{
    /// <summary>The numeric part of <see cref="Version"/>, without a prerelease label.</summary>
    public System.Version RuntimeVersion => ParseVersion(Version)?.Version ?? throw new InaException($"'{Version}' is not a valid runtime pack version.");

    /// <summary>The ILCompiler executable, a native program for the host machine.</summary>
    public string CompilerPath => Path.Combine(CompilerDirectory, "tools", OperatingSystem.IsWindows() ? "ilc.exe" : "ilc");

    /// <summary>Static libraries of the runtime and the NativeAOT flavor of the core library.</summary>
    public string NativeDirectory => Path.Combine(RuntimeDirectory, "runtimes", Target.RuntimeIdentifier, "native");

    /// <summary>The framework assemblies (IL) built for the target platform.</summary>
    public string FrameworkDirectory
    {
        get
        {
            var lib = Path.Combine(RuntimeDirectory, "runtimes", Target.RuntimeIdentifier, "lib");
            return Directory.Exists(lib) && Directory.EnumerateDirectories(lib, "net*").Order(StringComparer.Ordinal).LastOrDefault() is { } directory
                ? directory
                : throw new InaException($"The runtime pack in {RuntimeDirectory} does not contain framework assemblies for {Target.RuntimeIdentifier}.");
        }
    }

    public static string CompilerPackageId(string hostRuntimeIdentifier) => $"runtime.{hostRuntimeIdentifier}.Microsoft.DotNet.ILCompiler";

    public static string RuntimePackageId(TargetPlatform target) => $"Microsoft.NETCore.App.Runtime.NativeAOT.{target.RuntimeIdentifier}";

    /// <summary>
    /// Locates the packs for a target, downloading what is missing. Without an explicit version the newest patch
    /// release of the application's framework version is used, preferring one whose compiler is already on disk.
    /// </summary>
    public static async Task<RuntimePacks> ResolveAsync(
        PackageStore store, TargetPlatform target, System.Version frameworkVersion, string? requestedVersion, ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var compilerId = CompilerPackageId(TargetPlatform.HostRuntimeIdentifier);
        var runtimeId = RuntimePackageId(target);

        var version = requestedVersion
            ?? SelectVersion(store.InstalledVersions(compilerId), frameworkVersion)
            ?? SelectVersion(await store.AvailableVersionsAsync(runtimeId, cancellationToken).ConfigureAwait(false), frameworkVersion)
            ?? throw new InaException(
                $"No {runtimeId} package exists for .NET {frameworkVersion.Major}.{frameworkVersion.Minor}. " +
                "Ina supports applications targeting .NET 10 or later.");

        logger.LogDebug("Using runtime packs {Version} for {Target}", version, target.RuntimeIdentifier);
        var compiler = await store.GetPackageDirectoryAsync(compilerId, version, cancellationToken).ConfigureAwait(false);
        var runtime = await store.GetPackageDirectoryAsync(runtimeId, version, cancellationToken).ConfigureAwait(false);
        return new RuntimePacks(version, compiler, runtime, target);
    }

    /// <summary>Picks the newest version of the given major.minor line, preferring stable releases over previews.</summary>
    public static string? SelectVersion(IEnumerable<string> versions, System.Version frameworkVersion)
    {
        return versions
            .Select(static text => (Text: text, Parsed: ParseVersion(text)))
            .Where(v => v.Parsed is { } parsed && parsed.Version.Major == frameworkVersion.Major && parsed.Version.Minor == frameworkVersion.Minor)
            .OrderBy(static v => v.Parsed!.Value.IsStable)
            .ThenBy(static v => v.Parsed!.Value.Version)
            .ThenBy(static v => v.Text, StringComparer.OrdinalIgnoreCase)
            .Select(static v => v.Text)
            .LastOrDefault();
    }

    private static (System.Version Version, bool IsStable)? ParseVersion(string text)
    {
        var dash = text.IndexOf('-');
        return System.Version.TryParse(dash < 0 ? text : text[..dash], out var version) ? (version, dash < 0) : null;
    }
}
