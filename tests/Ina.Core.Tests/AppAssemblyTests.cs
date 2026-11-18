using Ina.Linker;

namespace Ina.Tests;

public class AppAssemblyTests
{
    /// <summary>The test project itself is an executable assembly.</summary>
    private static string ExecutableAssembly => typeof(AppAssemblyTests).Assembly.Location;

    /// <summary>A class library that happens to be next to the tests.</summary>
    private static string LibraryAssembly => typeof(ElfLinker).Assembly.Location;

    [Fact]
    public void ExecutableAssemblyHasNameEntryPointAndFramework()
    {
        var app = AppAssembly.Load(ExecutableAssembly);

        Assert.Equal("Ina.Core.Tests", app.Name);
        Assert.True(app.HasEntryPoint);
        Assert.Equal(new Version(10, 0), app.FrameworkVersion);
    }

    [Fact]
    public void LibraryHasNoEntryPoint()
    {
        Assert.False(AppAssembly.Load(LibraryAssembly).HasEntryPoint);
    }

    [Fact]
    public void FrameworkVersionComesFromTheAssemblyWhenThereIsNoRuntimeConfig()
    {
        using var directory = new TemporaryDirectory();
        var copy = directory.Combine("Library.dll");
        File.Copy(LibraryAssembly, copy);

        var app = AppAssembly.Load(copy);

        Assert.Equal(new Version(10, 0), app.FrameworkVersion);
        Assert.Empty(app.RuntimeKnobs);
    }

    [Fact]
    public void RuntimeConfigProvidesFrameworkVersionAndSettings()
    {
        using var directory = new TemporaryDirectory();
        var copy = directory.Combine("App.dll");
        File.Copy(LibraryAssembly, copy);
        directory.CreateFile("App.runtimeconfig.json", """
            {
              "runtimeOptions": {
                "tfm": "net11.0",
                "framework": { "name": "Microsoft.NETCore.App", "version": "11.0.0-preview.3" },
                "configProperties": {
                  "System.Globalization.Invariant": true,
                  "System.GC.Server": false,
                  "System.GC.HeapCount": 4,
                  "Custom.Setting": "text"
                }
              }
            }
            """);

        var app = AppAssembly.Load(copy);

        Assert.Equal(new Version(11, 0), app.FrameworkVersion);
        Assert.Equal("true", app.RuntimeKnobs["System.Globalization.Invariant"]);
        Assert.Equal("false", app.RuntimeKnobs["System.GC.Server"]);
        Assert.Equal("4", app.RuntimeKnobs["System.GC.HeapCount"]);
        Assert.Equal("text", app.RuntimeKnobs["Custom.Setting"]);
    }

    [Fact]
    public void LocalReferencesAreTheOtherManagedAssembliesNextToTheApplication()
    {
        using var directory = new TemporaryDirectory();
        var app = directory.Combine("App.dll");
        File.Copy(ExecutableAssembly, app);
        File.Copy(LibraryAssembly, directory.Combine("Dependency.dll"));
        directory.CreateFile("native.dll", "not a managed assembly");
        directory.CreateFile("readme.txt", "ignored");

        var references = AppAssembly.Load(app).LocalReferences;

        Assert.Equal([directory.Combine("Dependency.dll")], references);
    }

    [Fact]
    public void MissingFileIsReported()
    {
        var error = Assert.Throws<InaException>(() => AppAssembly.Load("/does/not/exist/App.dll"));

        Assert.Contains("not found", error.Message);
    }

    [Fact]
    public void FileThatIsNotAnAssemblyIsReported()
    {
        using var directory = new TemporaryDirectory();
        var file = directory.CreateFile("App.dll", "just some text");

        var error = Assert.Throws<InaException>(() => AppAssembly.Load(file));

        Assert.Contains("not a .NET assembly", error.Message);
    }

    [Fact]
    public void BrokenRuntimeConfigIsReported()
    {
        using var directory = new TemporaryDirectory();
        var copy = directory.Combine("App.dll");
        File.Copy(LibraryAssembly, copy);
        directory.CreateFile("App.runtimeconfig.json", "{ not json");

        var error = Assert.Throws<InaException>(() => AppAssembly.Load(copy));

        Assert.Contains("App.runtimeconfig.json", error.Message);
    }
}
