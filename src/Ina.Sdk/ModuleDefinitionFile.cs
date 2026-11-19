namespace Ina.Sdk;

/// <summary>
/// Reads the module-definition file that ILCompiler writes with <c>--exportsfile</c> for the Microsoft linker, which
/// takes it as <c>/DEF</c>:
/// <code>
/// EXPORTS
///    DotNetRuntimeContractDescriptor DATA
/// </code>
/// Only the <c>EXPORTS</c> statement is understood, with definitions that export a symbol under its own name.
/// </summary>
internal static class ModuleDefinitionFile
{
    private const string Exports = "EXPORTS";

    /// <summary>The other statements of the format. A name that equals one of them has to be quoted.</summary>
    private static readonly string[] OtherStatements = ["NAME", "LIBRARY", "HEAPSIZE", "SECTIONS", "STACKSIZE", "STUB", "VERSION"];

    /// <summary>The attributes that only change what an import library says about a name.</summary>
    private static readonly string[] ImportLibraryAttributes = ["DATA", "PRIVATE"];

    /// <summary>The names listed under <c>EXPORTS</c>, which are the symbols to export from the image.</summary>
    /// <exception cref="FormatException">The file uses something that the linker cannot honor.</exception>
    public static IReadOnlyList<string> ReadExports(string file)
    {
        var symbols = new List<string>();
        var inExports = false;
        foreach (var line in file.Split('\n'))
        {
            // A comment takes a line of its own; keywords and names are case sensitive.
            var tokens = line.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || tokens[0].StartsWith(';'))
            {
                continue;
            }

            if (tokens[0] == Exports)
            {
                inExports = true;
                tokens = tokens[1..];
                if (tokens.Length == 0)
                {
                    continue;
                }
            }

            if (OtherStatements.Contains(tokens[0]))
            {
                throw new FormatException($"The statement '{tokens[0]}' is not supported; only {Exports} is.");
            }

            if (!inExports)
            {
                throw new FormatException($"'{line.Trim()}' is not part of an {Exports} statement.");
            }

            symbols.Add(ReadDefinition(tokens, line.Trim()));
        }

        return symbols;
    }

    /// <summary>A definition: <c>entryname[=internal_name] [@ordinal [NONAME]] [PRIVATE] [DATA]</c>.</summary>
    private static string ReadDefinition(string[] tokens, string definition)
    {
        // The linker exports a symbol under the name it has; another name or an ordinal would be silently lost.
        if (definition.Contains('=') || definition.Contains('@'))
        {
            throw new FormatException($"The export '{definition}' is not supported; symbols are exported under their own name, without an ordinal.");
        }

        foreach (var attribute in tokens.AsSpan(1))
        {
            if (!ImportLibraryAttributes.Contains(attribute))
            {
                throw new FormatException($"The attribute '{attribute}' of the export '{definition}' is not supported.");
            }
        }

        return tokens[0].Trim('"');
    }
}
