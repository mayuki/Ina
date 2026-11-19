using Ina.Linker;

namespace Ina.Sdk;

/// <summary>
/// The targets the link task accepts. The same list guards <c>PublishIna</c> in Ina.Sdk.targets, where an unsupported
/// runtime identifier is rejected before anything is restored or compiled.
/// </summary>
internal static class SupportedTargets
{
    public static IReadOnlyList<string> RuntimeIdentifiers { get; } = ["linux-x64", "linux-arm64"];

    /// <summary>The architecture of a supported runtime identifier; all of them link against glibc.</summary>
    public static TargetArchitecture? Architecture(string runtimeIdentifier) => runtimeIdentifier.ToLowerInvariant() switch
    {
        "linux-x64" => TargetArchitecture.X64,
        "linux-arm64" => TargetArchitecture.Arm64,
        _ => null,
    };
}
