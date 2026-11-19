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
        Assert.Equal(new Version(11, 0), app.FrameworkVersion);
    }

    [Fact]
    public void GraphicalApplicationIsRecognizedByItsSubsystem()
    {
        // OutputType=WinExe only differs from Exe in the subsystem field of the PE header, which follows the
        // signature (4 bytes), the file header (20 bytes) and 68 bytes of the optional header.
        using var directory = new TemporaryDirectory();
        var image = File.ReadAllBytes(ExecutableAssembly);
        var subsystem = BitConverter.ToInt32(image, 0x3c) + 4 + 20 + 68;
        Assert.Equal(3, BitConverter.ToUInt16(image, subsystem)); // console
        image[subsystem] = 2;                                     // graphical
        var windowed = directory.Combine("Windowed.dll");
        File.WriteAllBytes(windowed, image);

        Assert.False(AppAssembly.Load(ExecutableAssembly).IsWindowsApplication);
        Assert.True(AppAssembly.Load(windowed).IsWindowsApplication);
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

        Assert.Equal(new Version(11, 0), app.FrameworkVersion);
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
                "tfm": "net12.0",
                "framework": { "name": "Microsoft.NETCore.App", "version": "12.0.0-preview.3" },
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

        Assert.Equal(new Version(12, 0), app.FrameworkVersion);
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
