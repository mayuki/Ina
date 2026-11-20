namespace Ina.Linker.Elf;

/// <summary>Rules that map input section names to output sections, mirroring the default GNU linker script.</summary>
internal static class SectionNaming
{
    // Longer prefixes must come before their shorter variants (".data.rel.ro" before ".data").
    private static readonly string[] MergedPrefixes =
    [
        ".text", ".rodata", ".data.rel.ro", ".data", ".bss.rel.ro", ".bss", ".init_array", ".fini_array", ".preinit_array",
        ".tdata", ".tbss", ".gcc_except_table", ".ctors", ".dtors", ".lrodata", ".ldata", ".lbss",
    ];

    /// <summary>Returns the name of the output section that <paramref name="inputName"/> is merged into.</summary>
    public static string OutputName(string inputName)
    {
        foreach (var prefix in MergedPrefixes)
        {
            if (inputName.StartsWith(prefix, StringComparison.Ordinal) && (inputName.Length == prefix.Length || inputName[prefix.Length] == '.'))
            {
                return prefix;
            }
        }

        return inputName;
    }

    /// <summary>
    /// Whether <c>__start_NAME</c> / <c>__stop_NAME</c> symbols are provided for a section of this name,
    /// which is the case when the name is a valid C identifier.
    /// </summary>
    public static bool IsCIdentifier(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty || char.IsAsciiDigit(name[0]))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Sort key for <c>.init_array.N</c> style sections: lower priorities run first, unnumbered sections last.
    /// </summary>
    public static int InitPriority(string inputName)
    {
        var dot = inputName.LastIndexOf('.');
        return dot > 0 && int.TryParse(inputName.AsSpan(dot + 1), out var priority) ? priority : 65536;
    }
}
