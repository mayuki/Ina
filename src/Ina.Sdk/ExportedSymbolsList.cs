namespace Ina.Sdk;

/// <summary>
/// Reads the list of symbols that ILCompiler writes with <c>--exportsfile</c> for Apple's linker, which takes it
/// as <c>-exported_symbols_list</c>: one symbol per line, spelled the way object files spell it.
/// <code>
/// _DotNetRuntimeDebugHeader
/// </code>
/// </summary>
internal static class ExportedSymbolsList
{
    /// <summary>The listed symbols the way C code spells them, which is how the linker takes them.</summary>
    /// <exception cref="FormatException">A line is a pattern, or a name that no C symbol has.</exception>
    public static IReadOnlyList<string> Read(string list)
    {
        var symbols = new List<string>();
        foreach (var line in list.Split('\n'))
        {
            // Apple's linker ignores everything behind a '#'.
            var comment = line.IndexOf('#');
            var name = (comment < 0 ? line : line[..comment]).Trim();
            if (name.Length == 0)
            {
                continue;
            }

            if (name.AsSpan().IndexOfAny('*', '?', '[') >= 0)
            {
                throw new FormatException($"The pattern '{name}' is not supported; exported symbols have to be listed by name.");
            }

            if (name.Length < 2 || name[0] != '_')
            {
                throw new FormatException($"The symbol '{name}' does not start with the underscore that every C name has in an object file.");
            }

            symbols.Add(name[1..]);
        }

        return symbols;
    }
}
