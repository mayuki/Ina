namespace Ina.Cli;

/// <summary>Helpers for the one-line result that is printed after a build.</summary>
internal static class Summary
{
    /// <summary>
    /// Derives the oldest system the executable runs on from what the linker reports as required: the newest
    /// symbol version of the C library, e.g. <c>libc.so.6:GLIBC_2.17</c> yields ", glibc 2.17+", or the minimum
    /// version of the operating system, e.g. <c>macOS 12.0</c> yields ", macOS 12.0+".
    /// </summary>
    public static string MinimumSystem(IEnumerable<string> requiredVersions)
    {
        const string prefix = "GLIBC_";
        const string macOS = "macOS ";
        Version? newest = null;
        foreach (var requirement in requiredVersions)
        {
            if (requirement.StartsWith(macOS, StringComparison.Ordinal))
            {
                return $", {requirement}+";
            }

            var index = requirement.IndexOf(prefix, StringComparison.Ordinal);
            if (index >= 0 && Version.TryParse(requirement[(index + prefix.Length)..], out var version) && (newest is null || version > newest))
            {
                newest = version;
            }
        }

        return newest is null ? string.Empty : $", glibc {newest}+";
    }
}
