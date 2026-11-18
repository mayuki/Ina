namespace Ina.Cli;

/// <summary>Helpers for the one-line result that is printed after a build.</summary>
internal static class Summary
{
    /// <summary>
    /// Derives the oldest C library the executable runs on from the symbol versions it requires,
    /// e.g. <c>libc.so.6:GLIBC_2.17</c> yields ", glibc 2.17+".
    /// </summary>
    public static string MinimumCLibrary(IEnumerable<string> requiredVersions)
    {
        const string prefix = "GLIBC_";
        Version? newest = null;
        foreach (var requirement in requiredVersions)
        {
            var index = requirement.IndexOf(prefix, StringComparison.Ordinal);
            if (index >= 0 && Version.TryParse(requirement[(index + prefix.Length)..], out var version) && (newest is null || version > newest))
            {
                newest = version;
            }
        }

        return newest is null ? string.Empty : $", glibc {newest}+";
    }
}
