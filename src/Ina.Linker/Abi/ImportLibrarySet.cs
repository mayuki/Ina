using System.Reflection;

namespace Ina.Linker.Abi;

/// <summary>The kind of an imported symbol, as far as the linker needs to know.</summary>
public enum ImportedSymbolKind
{
    Function,
    Object,
    Tls,

    /// <summary>The table does not say what the symbol is. Mach-O linking does not depend on it.</summary>
    Unspecified,
}

/// <summary>A symbol that a shared library provides at run time.</summary>
/// <param name="Library">
/// The name under which the loader finds the providing library: the SONAME for ELF, e.g. <c>libc.so.6</c>, and the
/// install name for Mach-O, e.g. <c>/usr/lib/libSystem.B.dylib</c>.
/// </param>
/// <param name="Version">The symbol version to bind to, e.g. <c>GLIBC_2.2.5</c>; null for unversioned symbols.</param>
public sealed record ImportedSymbol(string Library, string? Version, ImportedSymbolKind Kind);

/// <summary>
/// Describes the shared libraries an executable is linked against without needing the libraries themselves:
/// just the exported symbol names and their versions. This is what lets Ina link against the C library of any
/// target from any host, in the same spirit as Go's <c>cgo_import_dynamic</c>.
/// </summary>
public sealed class ImportLibrarySet
{
    private static readonly Version DefaultLibraryVersion = new(1, 0, 0);

    private readonly Dictionary<string, ImportedSymbol> _symbols = new(StringComparer.Ordinal);
    private readonly List<string> _libraries = [];
    private readonly Dictionary<string, (Version Compatibility, Version Current)> _libraryVersions = new(StringComparer.Ordinal);

    public ImportLibrarySet(string description, string interpreter)
    {
        Description = description;
        Interpreter = interpreter;
    }

    /// <summary>Human readable description, e.g. <c>glibc 2.27 x86_64</c>.</summary>
    public string Description { get; }

    /// <summary>Path of the dynamic loader on the target system (PT_INTERP, LC_LOAD_DYLINKER).</summary>
    public string Interpreter { get; }

    /// <summary>The oldest version of the operating system that provides everything in the table; null if not stated.</summary>
    public Version? MinimumOSVersion { get; init; }

    /// <summary>The version of the SDK the table was generated from; null if not stated.</summary>
    public Version? SdkVersion { get; init; }

    public int Count => _symbols.Count;

    public IEnumerable<KeyValuePair<string, ImportedSymbol>> Symbols => _symbols;

    /// <summary>The libraries in the order in which they are listed as dependencies (DT_NEEDED) when used.</summary>
    public IReadOnlyList<string> Libraries => _libraries;

    /// <summary>Declares a library. The order of the declarations is the dependency order.</summary>
    public void AddLibrary(string library)
    {
        if (!_libraries.Contains(library))
        {
            _libraries.Add(library);
        }
    }

    /// <summary>Adds a symbol. The first definition of a name wins, mirroring library search order.</summary>
    public void Add(string name, ImportedSymbol symbol)
    {
        AddLibrary(symbol.Library);
        _symbols.TryAdd(name, symbol);
    }

    public bool TryGet(string name, out ImportedSymbol symbol) => _symbols.TryGetValue(name, out symbol!);

    /// <summary>Records the versions of a Mach-O dynamic library, which executables state for every dependency.</summary>
    public void SetLibraryVersion(string library, Version compatibility, Version current)
    {
        AddLibrary(library);
        _libraryVersions[library] = (compatibility, current);
    }

    /// <summary>The compatibility and current version of a Mach-O dynamic library.</summary>
    public (Version Compatibility, Version Current) LibraryVersion(string library) =>
        _libraryVersions.TryGetValue(library, out var versions) ? versions : (DefaultLibraryVersion, DefaultLibraryVersion);

    /// <summary>Returns the import table of the macOS system libraries for <paramref name="architecture"/>.</summary>
    public static ImportLibrarySet ForMacOS(TargetArchitecture architecture) => LoadEmbedded(architecture switch
    {
        TargetArchitecture.X64 => "macos-x64",
        TargetArchitecture.Arm64 => "macos-arm64",
        _ => throw new LinkerException($"no import table for architecture {architecture}"),
    });

    /// <summary>Returns the import table of the GNU C library for <paramref name="architecture"/>.</summary>
    public static ImportLibrarySet ForGlibc(TargetArchitecture architecture) => LoadEmbedded(architecture switch
    {
        TargetArchitecture.X64 => "glibc-x64",
        TargetArchitecture.Arm64 => "glibc-arm64",
        _ => throw new LinkerException($"no import table for architecture {architecture}"),
    });

    /// <summary>
    /// Returns the import table of Windows: the system DLLs that a NativeAOT program links against and the names
    /// they export. It is the same for all architectures.
    /// </summary>
    public static ImportLibrarySet ForWindows() => LoadEmbedded("windows");

    /// <summary>Loads an embedded import table by name, e.g. <c>glibc-x64</c>.</summary>
    public static ImportLibrarySet LoadEmbedded(string name)
    {
        var resource = $"Ina.Linker.Abi.{name}.abi";
        using var stream = typeof(ImportLibrarySet).GetTypeInfo().Assembly.GetManifestResourceStream(resource)
            ?? throw new LinkerException($"no embedded import table named '{name}'");
        using var reader = new StreamReader(stream);
        return Parse(reader);
    }

    /// <summary>
    /// Parses the text format written by <see cref="AbiTableGenerator"/>:
    /// <code>
    /// target glibc 2.27 x86_64
    /// interp /lib64/ld-linux-x86-64.so.2
    /// needed libm.so.6 libc.so.6
    /// lib libc.so.6
    /// ver GLIBC_2.2.5
    /// F abort accept
    /// D stderr stdout
    /// </code>
    /// <c>needed</c> gives the dependency order; <c>F</c>, <c>D</c> and <c>T</c> list functions, data objects and
    /// thread-local variables of the current library and version. Tables of Mach-O libraries additionally use
    /// <code>
    /// platform macos 12.0 26.5
    /// lib /usr/lib/libSystem.B.dylib
    /// libver 1.0.0 1356.0.0
    /// S _abort _accept
    /// </code>
    /// for the minimum OS and SDK version, the compatibility and current version of a library, and symbols whose
    /// kind is not recorded.
    /// </summary>
    public static ImportLibrarySet Parse(TextReader reader)
    {
        var description = string.Empty;
        var interpreter = string.Empty;
        var needed = new List<string>();
        string? library = null;
        string? version = null;
        var entries = new List<(string Name, ImportedSymbol Symbol)>();
        var libraryVersions = new List<(string Library, Version Compatibility, Version Current)>();
        Version? minimumOS = null;
        Version? sdk = null;

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var separator = line.IndexOf(' ');
            var keyword = separator < 0 ? line : line[..separator];
            var rest = separator < 0 ? string.Empty : line[(separator + 1)..];
            switch (keyword)
            {
                case "target":
                    description = rest;
                    break;
                case "interp":
                    interpreter = rest;
                    break;
                case "needed":
                    needed.AddRange(rest.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                    break;
                case "lib":
                    library = rest;
                    version = null;
                    break;
                case "ver":
                    version = rest == "-" ? null : rest;
                    break;
                case "platform":
                {
                    var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 3 || !Version.TryParse(parts[1], out minimumOS) || !Version.TryParse(parts[2], out sdk))
                    {
                        throw new LinkerException("malformed import table: expected 'platform <name> <minimum version> <sdk version>'");
                    }

                    break;
                }

                case "libver":
                {
                    var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (library is null || parts.Length != 2 || !Version.TryParse(parts[0], out var compatibility) || !Version.TryParse(parts[1], out var current))
                    {
                        throw new LinkerException("malformed import table: expected 'libver <compatibility version> <current version>' after a 'lib' line");
                    }

                    libraryVersions.Add((library, compatibility, current));
                    break;
                }

                case "F" or "D" or "T" or "S":
                    if (library is null)
                    {
                        throw new LinkerException("malformed import table: symbols before any 'lib' line");
                    }

                    var kind = keyword switch
                    {
                        "F" => ImportedSymbolKind.Function,
                        "D" => ImportedSymbolKind.Object,
                        "T" => ImportedSymbolKind.Tls,
                        _ => ImportedSymbolKind.Unspecified,
                    };
                    var import = new ImportedSymbol(library, version, kind);
                    foreach (var name in rest.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        entries.Add((name, import));
                    }

                    break;
                default:
                    throw new LinkerException($"malformed import table: unknown keyword '{keyword}'");
            }
        }

        var set = new ImportLibrarySet(description, interpreter) { MinimumOSVersion = minimumOS, SdkVersion = sdk };
        foreach (var name in needed)
        {
            set.AddLibrary(name);
        }

        foreach (var (name, symbol) in entries)
        {
            set.Add(name, symbol);
        }

        foreach (var (name, compatibility, current) in libraryVersions)
        {
            set.SetLibraryVersion(name, compatibility, current);
        }

        return set;
    }
}
