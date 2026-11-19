using System.Reflection;

namespace Ina.EndToEnd.Tests;

/// <summary>How a project refers to Ina.</summary>
internal enum InaReference
{
    /// <summary><c>&lt;Sdk Name="Ina.Sdk" Version="..." /&gt;</c> in the project file.</summary>
    Sdk,

    /// <summary><c>&lt;PackageReference Include="Ina.Sdk" /&gt;</c> with central package management.</summary>
    Package,
}

/// <summary>
/// A copy of a sample application that is set up for publishing with Ina the way a user would do it: by adding
/// a reference to Ina.Sdk and a few properties to the existing project file.
/// </summary>
internal sealed class SampleProject : IDisposable
{
    private static readonly string SamplesDirectory = Path.GetFullPath(typeof(SampleProject).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(static a => a.Key == "SamplesDirectory").Value!);

    private readonly OutputDirectory _directory = new();
    private readonly PublishEnvironment _environment;

    /// <param name="sample">Directory of the sample below <c>samples</c>; it is copied including sibling projects.</param>
    /// <param name="project">Path of the project file of the application inside the sample.</param>
    /// <param name="properties">MSBuild properties (XML elements) to add to the project.</param>
    public SampleProject(PublishEnvironment environment, string sample, string project, InaReference reference, string properties = "<PublishIna>true</PublishIna>")
    {
        _environment = environment;
        CopyDirectory(Path.Combine(SamplesDirectory, sample), Path.Combine(_directory.Path, sample));
        File.Copy(Path.Combine(SamplesDirectory, "Directory.Build.props"), Path.Combine(_directory.Path, "Directory.Build.props"));

        // The feed with the package under test is added to the package sources the machine is configured with.
        File.WriteAllText(
            Path.Combine(_directory.Path, "nuget.config"),
            $"""
            <configuration>
              <packageSources>
                <add key="ina-e2e" value="{environment.Feed}" />
              </packageSources>
            </configuration>
            """);
        File.WriteAllText(
            Path.Combine(_directory.Path, "Directory.Packages.props"),
            $"""
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Ina.Sdk" Version="{environment.SdkVersion}" />
              </ItemGroup>
            </Project>
            """);

        ProjectFile = Path.Combine(_directory.Path, sample, project);
        var (sdk, items) = reference switch
        {
            InaReference.Sdk => ($"""<Sdk Name="Ina.Sdk" Version="{environment.SdkVersion}" />""", string.Empty),
            _ => (string.Empty, """<ItemGroup><PackageReference Include="Ina.Sdk" PrivateAssets="all" /></ItemGroup>"""),
        };

        // The reference goes right behind the opening tag, whichever SDK of .NET the project uses.
        const string end = "</Project>";
        var text = File.ReadAllText(ProjectFile);
        var opening = text.IndexOf("<Project Sdk=\"Microsoft.NET.Sdk", StringComparison.Ordinal);
        Assert.True(opening >= 0 && text.Contains(end, StringComparison.Ordinal), $"Unexpected shape of {ProjectFile}");
        var start = text.IndexOf('>', opening) + 1;
        File.WriteAllText(
            ProjectFile,
            text[..start] + sdk + text[start..].Replace(end, $"<PropertyGroup>{properties}</PropertyGroup>{items}{end}"));
    }

    public string ProjectFile { get; }

    public string ProjectDirectory => Path.GetDirectoryName(ProjectFile)!;

    public string PublishDirectory => Path.Combine(_directory.Path, "publish");

    /// <summary>The native executable in the publish directory.</summary>
    public string Executable => Path.Combine(PublishDirectory, Path.GetFileNameWithoutExtension(ProjectFile));

    /// <summary>The native executable in the publish directory when it was published for Windows.</summary>
    public string WindowsExecutable => Executable + ".exe";

    /// <summary>Runs <c>dotnet publish</c> for the project, with the output going to <see cref="PublishDirectory"/>.</summary>
    public Task<ProcessResult> PublishAsync(params string[] arguments) =>
        TestEnvironment.RunAsync("dotnet", _environment.CommandLineEnvironment, ["publish", ProjectFile, "-o", PublishDirectory, .. arguments]);

    public void Dispose() => _directory.Dispose();

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        // Build output of the samples in the repository must not leak into the copy.
        foreach (var directory in Directory.EnumerateDirectories(source).Where(static d => Path.GetFileName(d) is not ("bin" or "obj")))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
