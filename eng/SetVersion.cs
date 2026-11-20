#:property TreatWarningsAsErrors=false

// Sets the version of the packages (the ina tool and Ina.Sdk) in Directory.Build.props, and in the documentation
// wherever it shows how a project refers to Ina.Sdk. An SDK reference needs an exact version, so the documentation
// cannot leave it out; DocumentedVersionTests fails when the two disagree.
//
// usage (in the repository root): dotnet run eng/SetVersion.cs -- <version>
//
// Run it for the commit that a release is tagged from, see docs/Releasing.md.

using System.Text.RegularExpressions;

if (args.Length != 1 || !Regex.IsMatch(args[0], @"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$"))
{
    Console.Error.WriteLine("usage: dotnet run eng/SetVersion.cs -- <version>, for example 0.2.0 or 0.2.0-preview.1");
    return 1;
}

var version = args[0];

if (!File.Exists("Directory.Build.props") || !File.Exists("README.md"))
{
    Console.Error.WriteLine("Run this in the root of the repository.");
    return 1;
}

var changed = Replace("Directory.Build.props", new Regex("(?<=<Version>)[^<]+(?=</Version>)"));
if (changed == 0 && !File.ReadAllText("Directory.Build.props").Contains($"<Version>{version}</Version>"))
{
    Console.Error.WriteLine("Directory.Build.props has no <Version> to set.");
    return 1;
}

// <Sdk Name="Ina.Sdk" Version="..." /> and <PackageReference Include="Ina.Sdk" Version="..." /> in a project,
// "Ina.Sdk": "..." in global.json. Versions with the label "dev" are examples for packages that are built locally.
var mention = new Regex("(?<=Ina\\.Sdk\" Version=\"|\"Ina\\.Sdk\": \")[^\"]+(?<!-dev)(?=\")");
foreach (var file in Directory.EnumerateFiles("docs", "*.md", SearchOption.AllDirectories).Prepend("README.md"))
{
    changed += Replace(file, mention);
}

Console.WriteLine(changed == 0 ? $"Everything names {version} already." : $"Set the version to {version}.");
return 0;

// Rewrites the file only when something changes, and keeps its line endings and encoding by replacing in place.
int Replace(string file, Regex pattern)
{
    var text = File.ReadAllText(file);
    var count = 0;
    var replaced = pattern.Replace(text, match =>
    {
        if (match.Value != version)
        {
            count++;
        }

        return version;
    });

    if (count > 0)
    {
        File.WriteAllText(file, replaced);
        Console.WriteLine($"{file}: {count}");
    }

    return count;
}
