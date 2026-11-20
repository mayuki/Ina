using System.Reflection;
using System.Text.RegularExpressions;

namespace Ina.Sdk.Tests;

/// <summary>
/// A project names the version of Ina.Sdk it uses, and an SDK reference takes neither a range nor a wildcard, so
/// the documentation has to spell out a version. These tests keep it the one in Directory.Build.props, which
/// eng/SetVersion.cs changes together with the documentation.
/// </summary>
public partial class DocumentedVersionTests
{
    private static readonly Dictionary<string, string> Metadata = typeof(DocumentedVersionTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(static a => a.Key, static a => a.Value!);

    private static string RepositoryDirectory => Path.GetFullPath(Metadata["RepositoryDirectory"]);

    // <Sdk Name="Ina.Sdk" Version="..." /> and <PackageReference Include="Ina.Sdk" Version="..." /> in a project,
    // "Ina.Sdk": "..." in global.json.
    [GeneratedRegex("Ina\\.Sdk\" Version=\"(?<version>[^\"]+)\"|\"Ina\\.Sdk\": \"(?<version>[^\"]+)\"")]
    private static partial Regex SdkVersion();

    [Fact]
    public void TheDocumentationNamesTheVersionOfThePackages()
    {
        var mentions = DocumentationFiles().SelectMany(Mentions).ToList();

        Assert.All(mentions, mention => Assert.True(
            mention.Version == Metadata["PackageVersion"],
            $"{mention.File}({mention.Line}) names Ina.Sdk {mention.Version}, but the packages have the version {Metadata["PackageVersion"]}. Run eng/SetVersion.cs to change both."));
    }

    [Fact]
    public void TheReadmeShowsHowToReferToTheSdk()
    {
        // Guards the test above: it would pass if the pattern no longer matched what the documentation says.
        Assert.NotEmpty(Mentions(Path.Combine(RepositoryDirectory, "README.md")));
    }

    private static IEnumerable<string> DocumentationFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepositoryDirectory, "docs"), "*.md", SearchOption.AllDirectories)
            .Prepend(Path.Combine(RepositoryDirectory, "README.md"));

    private static IEnumerable<(string File, int Line, string Version)> Mentions(string file)
    {
        var lines = File.ReadAllLines(file);
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (Match match in SdkVersion().Matches(lines[i]))
            {
                // Versions with the label "dev" are examples for packages that are built locally.
                var version = match.Groups["version"].Value;
                if (!version.EndsWith("-dev", StringComparison.Ordinal))
                {
                    yield return (Path.GetRelativePath(RepositoryDirectory, file), i + 1, version);
                }
            }
        }
    }
}
