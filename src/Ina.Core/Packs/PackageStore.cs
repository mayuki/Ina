using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Ina.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Ina.Packs;

/// <summary>
/// Finds NuGet packages on disk and downloads the ones that are missing. Ina only consumes packages: the compiler
/// (ILCompiler) and the runtime libraries of the target platform.
/// </summary>
public sealed class PackageStore : IDisposable
{
    public const string DefaultSource = "https://api.nuget.org/v3-flatcontainer/";

    private readonly string _cacheDirectory;
    private readonly IReadOnlyList<string> _probeDirectories;
    private readonly Uri _source;
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    /// <param name="cacheDirectory">Where downloaded packages are extracted to.</param>
    /// <param name="probeDirectories">Additional read-only package folders, such as the NuGet global packages folder.</param>
    /// <param name="source">Base address of a NuGet v3 flat container.</param>
    public PackageStore(string cacheDirectory, IReadOnlyList<string> probeDirectories, Uri source, ILogger logger, HttpMessageHandler? handler = null)
    {
        _cacheDirectory = cacheDirectory;
        _probeDirectories = probeDirectories;
        _source = source;
        _logger = logger;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ina");
    }

    /// <summary>
    /// Creates a store with the default locations: packages are cached in <c>~/.ina/packs</c> (or <c>INA_PACKS</c>)
    /// and the NuGet global packages folder is reused when it already contains a package.
    /// </summary>
    public static PackageStore CreateDefault(ILogger logger)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cache = Environment.GetEnvironmentVariable("INA_PACKS") is { Length: > 0 } packs ? packs : Path.Combine(home, ".ina", "packs");
        var nuget = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } packages ? packages : Path.Combine(home, ".nuget", "packages");
        var source = Environment.GetEnvironmentVariable("INA_NUGET_SOURCE") is { Length: > 0 } custom ? custom : DefaultSource;
        return new PackageStore(cache, [nuget], new Uri(source.EndsWith('/') ? source : source + "/"), logger);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Versions of a package that are already on disk.</summary>
    public IReadOnlyList<string> InstalledVersions(string id)
    {
        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in _probeDirectories.Prepend(_cacheDirectory))
        {
            var directory = Path.Combine(root, id.ToLowerInvariant());
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var version in Directory.EnumerateDirectories(directory))
            {
                if (IsComplete(version))
                {
                    versions.Add(Path.GetFileName(version));
                }
            }
        }

        return [.. versions];
    }

    /// <summary>Versions of a package that the package source offers.</summary>
    public async Task<IReadOnlyList<string>> AvailableVersionsAsync(string id, CancellationToken cancellationToken)
    {
        var url = new Uri(_source, $"{id.ToLowerInvariant()}/index.json");
        _logger.LogDebug("Querying {Url}", url);
        try
        {
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return [];
            }

            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return document.RootElement.GetProperty("versions").EnumerateArray().Select(static v => v.GetString()!).ToList();
        }
        catch (HttpRequestException e)
        {
            throw new InaException($"Could not query the versions of {id} from {_source}: {e.Message}", e);
        }
    }

    /// <summary>Returns the directory of an extracted package, downloading it first when necessary.</summary>
    public async Task<string> GetPackageDirectoryAsync(string id, string version, CancellationToken cancellationToken)
    {
        var relative = Path.Combine(id.ToLowerInvariant(), version.ToLowerInvariant());
        foreach (var root in _probeDirectories.Prepend(_cacheDirectory))
        {
            var candidate = Path.Combine(root, relative);
            if (IsComplete(candidate))
            {
                return candidate;
            }
        }

        var destination = Path.Combine(_cacheDirectory, relative);
        await DownloadAsync(id, version, destination, cancellationToken).ConfigureAwait(false);
        return destination;
    }

    /// <summary>
    /// A package directory counts only when extraction finished. Ina writes a marker; NuGet writes its own metadata
    /// file as the last step of an extraction.
    /// </summary>
    private static bool IsComplete(string directory) =>
        File.Exists(Path.Combine(directory, ".ina-complete")) || File.Exists(Path.Combine(directory, ".nupkg.metadata"));

    private async Task DownloadAsync(string id, string version, string destination, CancellationToken cancellationToken)
    {
        using var activity = InaTelemetry.ActivitySource.StartActivity("ina.download-package");
        activity?.SetTag("ina.package.id", id);
        activity?.SetTag("ina.package.version", version);

        var lowerId = id.ToLowerInvariant();
        var lowerVersion = version.ToLowerInvariant();
        var url = new Uri(_source, $"{lowerId}/{lowerVersion}/{lowerId}.{lowerVersion}.nupkg");
        _logger.LogInformation("Downloading {Id} {Version}", id, version);

        // Extract next to the destination and rename, so that an interrupted download never looks like a package.
        var staging = $"{destination}.{Guid.NewGuid():N}.tmp";
        Directory.CreateDirectory(staging);
        try
        {
            var archive = Path.Combine(staging, "package.nupkg");
            using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new InaException($"Package {id} {version} does not exist on {_source}.");
                }

                response.EnsureSuccessStatusCode();
                await using var file = File.Create(archive);
                await response.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            await ZipFile.ExtractToDirectoryAsync(archive, staging, cancellationToken).ConfigureAwait(false);
            File.Delete(archive);
            MakeToolsExecutable(staging);
            await File.WriteAllTextAsync(Path.Combine(staging, ".ina-complete"), string.Empty, cancellationToken).ConfigureAwait(false);

            Install(staging, destination);
        }
        catch (HttpRequestException e)
        {
            throw new InaException($"Could not download {id} {version} from {url}: {e.Message}", e);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    /// <summary>
    /// Puts an extracted package in place. Several builds can download the same package at the same time, each
    /// into its own staging directory. The rename is atomic, so whoever finishes first provides the package, and
    /// the others leave it alone: it may already be in use by the build that installed it.
    /// </summary>
    private static void Install(string staging, string destination)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; !IsComplete(destination); attempt++)
        {
            try
            {
                // Whatever is there is not a package, e.g. the remains of an extraction that was interrupted.
                if (Directory.Exists(destination))
                {
                    Directory.Delete(destination, recursive: true);
                }

                Directory.Move(staging, destination);
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                // Another build was changing the directory at the same moment. Look at what is there now.
            }
        }
    }

    /// <summary>Zip archives do not carry Unix permissions, but the compiler in <c>tools/</c> has to be executable.</summary>
    private static void MakeToolsExecutable(string packageDirectory)
    {
        var tools = Path.Combine(packageDirectory, "tools");
        if (OperatingSystem.IsWindows() || !Directory.Exists(tools))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(tools))
        {
            File.SetUnixFileMode(file, File.GetUnixFileMode(file) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }
}
