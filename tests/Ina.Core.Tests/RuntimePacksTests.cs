using System.IO.Compression;
using System.Net;
using System.Text;
using Ina.Linker;
using Ina.Packs;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ina.Tests;

public class RuntimePacksTests
{
    private static readonly TargetPlatform LinuxArm64 = new(TargetOperatingSystem.Linux, TargetArchitecture.Arm64);
    private static readonly Uri Feed = new("https://packages.test/flat/");
    private static readonly string CompilerId = RuntimePacks.CompilerPackageId(TargetPlatform.HostRuntimeIdentifier);
    private static readonly string RuntimeId = RuntimePacks.RuntimePackageId(LinuxArm64);

    [Theory]
    [InlineData("10.0.1,10.0.15,10.0.9,9.0.30,11.0.0", "10.0.15")]
    [InlineData("10.0.0-rc.2.25502.107,10.0.0", "10.0.0")]
    [InlineData("10.0.2,10.0.3-servicing.1", "10.0.2")]
    [InlineData("11.0.0-preview.1,11.0.0-preview.2", "11.0.0-preview.2")]
    [InlineData("9.0.1,11.0.0", null)]
    [InlineData("", null)]
    public void NewestPatchOfTheFrameworkLineIsSelected(string available, string? expected)
    {
        var framework = expected?.StartsWith("11", StringComparison.Ordinal) == true ? new Version(11, 0) : new Version(10, 0);

        Assert.Equal(expected, RuntimePacks.SelectVersion(available.Split(',', StringSplitOptions.RemoveEmptyEntries), framework));
    }

    [Fact]
    public void PackageIdsFollowTheNamingOfTheDotNetPacks()
    {
        Assert.Equal("runtime.linux-x64.Microsoft.DotNet.ILCompiler", RuntimePacks.CompilerPackageId("linux-x64"));
        Assert.Equal("Microsoft.NETCore.App.Runtime.NativeAOT.linux-arm64", RuntimeId);
    }

    [Fact]
    public async Task InstalledPacksAreUsedWithoutContactingThePackageSource()
    {
        using var directory = new TemporaryDirectory();
        var nuget = directory.Combine("nuget");
        InstallPackage(directory, "nuget", CompilerId, "10.0.3");
        InstallPackage(directory, "nuget", CompilerId, "10.0.7");
        InstallPackage(directory, "nuget", RuntimeId, "10.0.7");
        using var store = new PackageStore(directory.Combine("cache"), [nuget], Feed, NullLogger.Instance, new OfflineHandler());

        var packs = await RuntimePacks.ResolveAsync(store, LinuxArm64, new Version(10, 0), requestedVersion: null, NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Equal("10.0.7", packs.Version);
        Assert.Equal(Path.Combine(nuget, CompilerId.ToLowerInvariant(), "10.0.7"), packs.CompilerDirectory);
        Assert.Equal(Path.Combine(packs.CompilerDirectory, "tools", OperatingSystem.IsWindows() ? "ilc.exe" : "ilc"), packs.CompilerPath);
        Assert.Equal(Path.Combine(packs.RuntimeDirectory, "runtimes", "linux-arm64", "native"), packs.NativeDirectory);
    }

    [Fact]
    public async Task MissingPacksAreDownloadedIntoTheCache()
    {
        using var directory = new TemporaryDirectory();
        var handler = new FeedHandler();
        handler.AddVersions(RuntimeId, "10.0.1", "10.0.2", "11.0.0-preview.1");
        handler.AddPackage(CompilerId, "10.0.2", ("tools/ilc", "compiler"), ("tools/libjit.so", "jit"));
        handler.AddPackage(RuntimeId, "10.0.2",
            ("runtimes/linux-arm64/native/libbootstrapper.o", "object"), ("runtimes/linux-arm64/lib/net10.0/System.Runtime.dll", "assembly"));
        using var store = new PackageStore(directory.Combine("cache"), [directory.Combine("nuget")], Feed, NullLogger.Instance, handler);

        var packs = await RuntimePacks.ResolveAsync(store, LinuxArm64, new Version(10, 0), requestedVersion: null, NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Equal("10.0.2", packs.Version);
        Assert.StartsWith(directory.Combine("cache"), packs.CompilerDirectory);
        Assert.Equal("compiler", File.ReadAllText(packs.CompilerPath.Replace(".exe", string.Empty)));
        Assert.True(File.Exists(Path.Combine(packs.NativeDirectory, "libbootstrapper.o")));
        Assert.Equal(Path.Combine(packs.RuntimeDirectory, "runtimes", "linux-arm64", "lib", "net10.0"), packs.FrameworkDirectory);
        if (!OperatingSystem.IsWindows())
        {
            // Zip archives carry no permissions; the compiler must still be runnable.
            Assert.True((File.GetUnixFileMode(packs.CompilerPath) & UnixFileMode.UserExecute) != 0);
        }

        // A second resolution is served from the cache.
        var requests = handler.Requests.Count;
        await RuntimePacks.ResolveAsync(store, LinuxArm64, new Version(10, 0), requestedVersion: null, NullLogger.Instance, TestContext.Current.CancellationToken);
        Assert.Equal(requests, handler.Requests.Count);
    }

    [Fact]
    public async Task ExplicitVersionIsUsedAsIs()
    {
        using var directory = new TemporaryDirectory();
        InstallPackage(directory, "nuget", CompilerId, "10.0.3");
        InstallPackage(directory, "nuget", RuntimeId, "10.0.3");
        InstallPackage(directory, "nuget", CompilerId, "10.0.7");
        InstallPackage(directory, "nuget", RuntimeId, "10.0.7");
        using var store = new PackageStore(directory.Combine("cache"), [directory.Combine("nuget")], Feed, NullLogger.Instance, new OfflineHandler());

        var packs = await RuntimePacks.ResolveAsync(store, LinuxArm64, new Version(10, 0), "10.0.3", NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Equal("10.0.3", packs.Version);
    }

    [Fact]
    public async Task UnsupportedFrameworkVersionIsReported()
    {
        using var directory = new TemporaryDirectory();
        var handler = new FeedHandler();
        handler.AddVersions(RuntimeId, "10.0.1");
        using var store = new PackageStore(directory.Combine("cache"), [], Feed, NullLogger.Instance, handler);

        var error = await Assert.ThrowsAsync<InaException>(() =>
            RuntimePacks.ResolveAsync(store, LinuxArm64, new Version(8, 0), requestedVersion: null, NullLogger.Instance, TestContext.Current.CancellationToken));

        Assert.Contains(".NET 8.0", error.Message);
    }

    [Fact]
    public async Task PackageThatDoesNotExistIsReported()
    {
        using var directory = new TemporaryDirectory();
        using var store = new PackageStore(directory.Combine("cache"), [], Feed, NullLogger.Instance, new FeedHandler());

        var error = await Assert.ThrowsAsync<InaException>(() => store.GetPackageDirectoryAsync("Some.Package", "1.2.3", TestContext.Current.CancellationToken));

        Assert.Contains("Some.Package 1.2.3 does not exist", error.Message);

        // Nothing that could be mistaken for the package is left behind.
        Assert.Empty(store.InstalledVersions("Some.Package"));
        Assert.Empty(Directory.EnumerateFiles(directory.Combine("cache"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task InterruptedExtractionIsNotMistakenForAPackage()
    {
        using var directory = new TemporaryDirectory();

        // A directory without a completion marker, as left behind by a crashed download.
        directory.CreateFile(Path.Combine("cache", "some.package", "1.0.0", "partial.txt"));
        var handler = new FeedHandler();
        handler.AddPackage("Some.Package", "1.0.0", ("content.txt", "complete"));
        using var store = new PackageStore(directory.Combine("cache"), [], Feed, NullLogger.Instance, handler);

        Assert.Empty(store.InstalledVersions("Some.Package"));
        var package = await store.GetPackageDirectoryAsync("Some.Package", "1.0.0", TestContext.Current.CancellationToken);

        Assert.Equal("complete", File.ReadAllText(Path.Combine(package, "content.txt")));
        Assert.False(File.Exists(Path.Combine(package, "partial.txt")));
        Assert.Equal(["1.0.0"], store.InstalledVersions("Some.Package"));
    }


    [Fact]
    public async Task PackageThatAnotherBuildInstalledInTheMeantimeIsKept()
    {
        // Two builds that need a package for the first time both download it. The one that finishes later must not
        // replace the directory, because the other build is already compiling with what is in there.
        using var directory = new TemporaryDirectory();
        var release = new TaskCompletionSource();
        var fastFeed = new FeedHandler();
        var slowFeed = new FeedHandler { Gate = release.Task };
        fastFeed.AddPackage(CompilerId, "10.0.7", ("tools/ilc", "compiler"));
        slowFeed.AddPackage(CompilerId, "10.0.7", ("tools/ilc", "compiler"));
        using var first = new PackageStore(directory.Combine("cache"), [], Feed, NullLogger.Instance, fastFeed);
        using var second = new PackageStore(directory.Combine("cache"), [], Feed, NullLogger.Instance, slowFeed);

        // The second build has found the package missing and waits for its download.
        var pending = second.GetPackageDirectoryAsync(CompilerId, "10.0.7", TestContext.Current.CancellationToken);
        var installed = await first.GetPackageDirectoryAsync(CompilerId, "10.0.7", TestContext.Current.CancellationToken);
        var fileInUse = Path.Combine(installed, "tools", "in-use-by-the-first-build");
        File.WriteAllText(fileInUse, string.Empty);
        release.SetResult();

        Assert.Equal(installed, await pending);
        Assert.True(File.Exists(fileInUse));
        Assert.Equal("compiler", File.ReadAllText(Path.Combine(installed, "tools", "ilc")));

        // The download that came second is discarded rather than left behind.
        Assert.Equal([installed], Directory.GetDirectories(Path.GetDirectoryName(installed)!));
    }

    private static void InstallPackage(TemporaryDirectory directory, string root, string id, string version)
    {
        directory.CreateFile(Path.Combine(root, id.ToLowerInvariant(), version, ".nupkg.metadata"), "{}");
    }

    /// <summary>Fails every request: proves that no network access happens.</summary>
    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException($"unexpected request to {request.RequestUri}");
    }

    /// <summary>An in-memory NuGet flat container.</summary>
    private sealed class FeedHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _responses = new(StringComparer.Ordinal);

        public List<string> Requests { get; } = [];

        /// <summary>Package downloads are answered once this task completes, which lets a test order concurrent downloads.</summary>
        public Task Gate { get; init; } = Task.CompletedTask;

        public void AddVersions(string id, params string[] versions)
        {
            var json = $$"""{"versions":[{{string.Join(",", versions.Select(static v => $"\"{v}\""))}}]}""";
            _responses[$"{Feed}{id.ToLowerInvariant()}/index.json"] = Encoding.UTF8.GetBytes(json);
        }

        public void AddPackage(string id, string version, params (string Path, string Content)[] files)
        {
            using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (path, content) in files)
                {
                    using var entry = archive.CreateEntry(path).Open();
                    entry.Write(Encoding.UTF8.GetBytes(content));
                }
            }

            var lowerId = id.ToLowerInvariant();
            _responses[$"{Feed}{lowerId}/{version}/{lowerId}.{version}.nupkg"] = stream.ToArray();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            if (url.EndsWith(".nupkg", StringComparison.Ordinal))
            {
                await Gate.WaitAsync(cancellationToken);
            }

            return _responses.TryGetValue(url, out var content)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
