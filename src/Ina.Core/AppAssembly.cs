using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace Ina;

/// <summary>What Ina needs to know about the application that is compiled.</summary>
public sealed class AppAssembly
{
    private AppAssembly(string path, string name, bool hasEntryPoint, Version? frameworkVersion, IReadOnlyDictionary<string, string> runtimeKnobs)
    {
        Path = path;
        Name = name;
        HasEntryPoint = hasEntryPoint;
        FrameworkVersion = frameworkVersion;
        RuntimeKnobs = runtimeKnobs;
    }

    public string Path { get; }

    /// <summary>The simple name of the assembly, which also is the default name of the executable.</summary>
    public string Name { get; }

    public bool HasEntryPoint { get; }

    /// <summary>The version of Microsoft.NETCore.App the application targets (major.minor), if it could be determined.</summary>
    public Version? FrameworkVersion { get; }

    /// <summary>The <c>configProperties</c> of the application's runtimeconfig.json.</summary>
    public IReadOnlyDictionary<string, string> RuntimeKnobs { get; }

    /// <summary>Other managed assemblies in the directory of the application.</summary>
    public IReadOnlyList<string> LocalReferences => field ??= FindLocalReferences();

    public static AppAssembly Load(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        if (!File.Exists(path))
        {
            throw new InaException($"Assembly not found: {path}");
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
            {
                throw new InaException($"{path} is not a .NET assembly.");
            }

            var metadata = pe.GetMetadataReader();
            if (!metadata.IsAssembly)
            {
                throw new InaException($"{path} is a module, not an assembly.");
            }

            var name = metadata.GetString(metadata.GetAssemblyDefinition().Name);
            var hasEntryPoint = pe.PEHeaders.CorHeader?.EntryPointTokenOrRelativeVirtualAddress is not (null or 0);
            var (knobs, configuredVersion) = ReadRuntimeConfig(System.IO.Path.ChangeExtension(path, ".runtimeconfig.json"));
            return new AppAssembly(path, name, hasEntryPoint, configuredVersion ?? ReadTargetFramework(metadata), knobs);
        }
        catch (BadImageFormatException e)
        {
            throw new InaException($"{path} is not a .NET assembly.", e);
        }
    }

    /// <summary>Whether a file is a managed assembly (as opposed to a native library with the same extension).</summary>
    public static bool IsManagedAssembly(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            return pe.HasMetadata && pe.GetMetadataReader().IsAssembly;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    private List<string> FindLocalReferences()
    {
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        return Directory.EnumerateFiles(directory, "*.dll")
            .Where(file => !string.Equals(file, Path, StringComparison.OrdinalIgnoreCase) && IsManagedAssembly(file))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Reads the version out of <c>[TargetFramework(".NETCoreApp,Version=v10.0")]</c>.</summary>
    private static Version? ReadTargetFramework(MetadataReader metadata)
    {
        foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference)
            {
                continue;
            }

            var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (constructor.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            var type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
            if (metadata.GetString(type.Name) != "TargetFrameworkAttribute")
            {
                continue;
            }

            // Custom attribute blob: prolog (0x0001) followed by the fixed arguments, here a single string.
            var blob = metadata.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1 || blob.ReadSerializedString() is not { } framework)
            {
                continue;
            }

            const string prefix = ".NETCoreApp,Version=v";
            if (framework.StartsWith(prefix, StringComparison.Ordinal) && Version.TryParse(framework[prefix.Length..], out var version))
            {
                return version;
            }
        }

        return null;
    }

    private static (IReadOnlyDictionary<string, string> Knobs, Version? FrameworkVersion) ReadRuntimeConfig(string path)
    {
        var knobs = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return (knobs, null);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!document.RootElement.TryGetProperty("runtimeOptions", out var options))
            {
                return (knobs, null);
            }

            Version? version = null;
            IEnumerable<JsonElement> frameworks =
                options.TryGetProperty("framework", out var single) ? [single]
                : options.TryGetProperty("frameworks", out var many) && many.ValueKind == JsonValueKind.Array ? many.EnumerateArray()
                : [];
            foreach (var framework in frameworks)
            {
                if (framework.TryGetProperty("name", out var name) && name.GetString() == "Microsoft.NETCore.App"
                    && framework.TryGetProperty("version", out var value) && Version.TryParse(value.GetString()?.Split('-')[0], out var parsed))
                {
                    version = new Version(parsed.Major, parsed.Minor);
                }
            }

            if (options.TryGetProperty("configProperties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in properties.EnumerateObject())
                {
                    knobs[property.Name] = property.Value.ValueKind switch
                    {
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                        _ => property.Value.GetRawText(),
                    };
                }
            }

            return (knobs, version);
        }
        catch (JsonException e)
        {
            throw new InaException($"{path} is not valid JSON: {e.Message}", e);
        }
    }
}
