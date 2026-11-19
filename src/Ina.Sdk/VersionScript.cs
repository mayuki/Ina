namespace Ina.Sdk;

/// <summary>
/// Reads the version script that ILCompiler writes with <c>--exportsfile</c> for GNU-style linkers:
/// <code>
/// V1.0 {
///     global:
///         DotNetRuntimeDebugHeader;
///     local: *;
/// };
/// </code>
/// Only this shape is understood; it is not a general parser of version scripts.
/// </summary>
internal static class VersionScript
{
    private static readonly char[] Punctuation = ['{', '}', ';', ':'];

    /// <summary>The names listed under <c>global:</c>, which are the symbols to export from the image.</summary>
    /// <exception cref="FormatException">A pattern is used where a name is expected.</exception>
    public static IReadOnlyList<string> ReadGlobalSymbols(string script)
    {
        var symbols = new List<string>();
        var tokens = Tokenize(script);
        var inGlobalScope = false;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (i + 1 < tokens.Count && tokens[i + 1] == ":")
            {
                // A label such as "global:" or "local:" starts a new scope.
                inGlobalScope = token == "global";
                i++;
            }
            else if (token == "}")
            {
                inGlobalScope = false;
            }
            else if (inGlobalScope && token != ";")
            {
                if (token.AsSpan().IndexOfAny('*', '?', '[') >= 0)
                {
                    throw new FormatException($"The pattern '{token}' is not supported; exported symbols have to be listed by name.");
                }

                symbols.Add(token);
            }
        }

        return symbols;
    }

    private static List<string> Tokenize(string script)
    {
        var tokens = new List<string>();
        var start = -1;
        for (var i = 0; i <= script.Length; i++)
        {
            var isSeparator = i == script.Length || char.IsWhiteSpace(script[i]) || Array.IndexOf(Punctuation, script[i]) >= 0;
            if (!isSeparator)
            {
                start = start < 0 ? i : start;
                continue;
            }

            if (start >= 0)
            {
                tokens.Add(script[start..i]);
                start = -1;
            }

            if (i < script.Length && !char.IsWhiteSpace(script[i]))
            {
                tokens.Add(script[i].ToString());
            }
        }

        return tokens;
    }
}
