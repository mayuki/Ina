using Ina.Linker;

namespace Ina.Sdk;

/// <summary>The operating system of a target. It decides the object format and what stands in for the system libraries.</summary>
internal enum TargetSystem
{
    Linux,
    MacOS,
}

/// <summary>What the link task has to know about a runtime identifier.</summary>
internal readonly record struct SupportedTarget(TargetSystem System, TargetArchitecture Architecture);

/// <summary>
/// The targets the link task accepts. The same list guards <c>PublishIna</c> in Ina.Sdk.targets, where an unsupported
/// runtime identifier is rejected before anything is restored or compiled.
/// </summary>
internal static class SupportedTargets
{
    public static IReadOnlyList<string> RuntimeIdentifiers { get; } = ["linux-x64", "linux-arm64", "osx-x64", "osx-arm64"];

    /// <summary>The target of a supported runtime identifier. Linux means glibc; musl has identifiers of its own.</summary>
    public static SupportedTarget? Find(string runtimeIdentifier) => runtimeIdentifier.ToLowerInvariant() switch
    {
        "linux-x64" => new(TargetSystem.Linux, TargetArchitecture.X64),
        "linux-arm64" => new(TargetSystem.Linux, TargetArchitecture.Arm64),
        "osx-x64" => new(TargetSystem.MacOS, TargetArchitecture.X64),
        "osx-arm64" => new(TargetSystem.MacOS, TargetArchitecture.Arm64),
        _ => null,
    };
}
