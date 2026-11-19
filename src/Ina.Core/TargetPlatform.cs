using System.Runtime.InteropServices;
using Ina.Linker;

namespace Ina;

/// <summary>Operating systems Ina can produce executables for.</summary>
public enum TargetOperatingSystem
{
    Linux,
    MacOS,
    Windows,
}

/// <summary>The platform an executable is built for.</summary>
public sealed record TargetPlatform(TargetOperatingSystem OperatingSystem, TargetArchitecture Architecture)
{
    /// <summary>The .NET runtime identifier, e.g. <c>linux-x64</c>, <c>osx-arm64</c> or <c>win-arm64</c>.</summary>
    public string RuntimeIdentifier => $"{OperatingSystemName}-{ArchitectureName}";

    /// <summary>
    /// The name ILCompiler uses for the operating system (<c>--targetos</c>), which is also how runtime identifiers
    /// spell it.
    /// </summary>
    public string OperatingSystemName => OperatingSystem switch
    {
        TargetOperatingSystem.Linux => "linux",
        TargetOperatingSystem.MacOS => "osx",
        TargetOperatingSystem.Windows => "win",
        _ => throw new InaException($"Unsupported target operating system: {OperatingSystem}"),
    };

    /// <summary>
    /// The native library of the framework that implements cryptography: on top of OpenSSL on Linux, on top of the
    /// system frameworks on macOS. On Windows the framework calls the system libraries directly.
    /// </summary>
    public string CryptographyLibrary => OperatingSystem switch
    {
        TargetOperatingSystem.Linux => "System.Security.Cryptography.Native.OpenSsl",
        TargetOperatingSystem.MacOS => "System.Security.Cryptography.Native.Apple",
        _ => throw new InaException($"Unsupported target operating system: {OperatingSystem}"),
    };

    /// <summary>The extension that executables have on the target system, including the dot.</summary>
    public string ExecutableExtension => OperatingSystem == TargetOperatingSystem.Windows ? ".exe" : string.Empty;

    /// <summary>The conventional extension of object files for the target system.</summary>
    public string ObjectExtension => OperatingSystem == TargetOperatingSystem.Windows ? ".obj" : ".o";

    /// <summary>The name ILCompiler uses for the architecture (<c>--targetarch</c>).</summary>
    public string ArchitectureName => Architecture switch
    {
        TargetArchitecture.X64 => "x64",
        TargetArchitecture.Arm64 => "arm64",
        _ => throw new InaException($"Unsupported target architecture: {Architecture}"),
    };

    public static TargetPlatform Parse(string? operatingSystem, string? architecture)
    {
        var os = (operatingSystem ?? HostOperatingSystemName).ToLowerInvariant() switch
        {
            "linux" => TargetOperatingSystem.Linux,
            "macos" or "osx" => TargetOperatingSystem.MacOS,
            "windows" or "win" => TargetOperatingSystem.Windows,
            var other => throw new InaException($"Unsupported target operating system '{other}'. Supported: linux, macos, windows."),
        };

        var arch = (architecture ?? HostArchitectureName).ToLowerInvariant() switch
        {
            "x64" or "amd64" or "x86_64" => TargetArchitecture.X64,
            "arm64" or "aarch64" => TargetArchitecture.Arm64,
            var other => throw new InaException($"Unsupported target architecture '{other}'. Supported: x64, arm64."),
        };

        return new TargetPlatform(os, arch);
    }

    /// <summary>
    /// The runtime identifier of the machine Ina runs on. It selects the ILCompiler package, whose compiler is a
    /// native program for the host; the code it generates can target any platform.
    /// </summary>
    public static string HostRuntimeIdentifier
    {
        get
        {
            var os = System.OperatingSystem.IsWindows() ? "win"
                : System.OperatingSystem.IsMacOS() ? "osx"
                : RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.Ordinal) ? "linux-musl"
                : "linux";
            return $"{os}-{HostArchitectureName}";
        }
    }

    /// <summary>
    /// The target that is built when none is chosen: the operating system of this machine if Ina can build for it,
    /// and Linux otherwise.
    /// </summary>
    private static string HostOperatingSystemName => System.OperatingSystem.IsWindows() ? "windows"
        : System.OperatingSystem.IsMacOS() ? "macos"
        : "linux";

    private static string HostArchitectureName => RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "x64",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        var other => throw new InaException($"Ina does not run on {other} hosts."),
    };
}
