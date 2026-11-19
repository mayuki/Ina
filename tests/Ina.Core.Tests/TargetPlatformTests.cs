using System.Runtime.InteropServices;
using Ina.Linker;

namespace Ina.Tests;

public class TargetPlatformTests
{
    [Theory]
    [InlineData("linux", "x64", "linux-x64")]
    [InlineData("Linux", "AMD64", "linux-x64")]
    [InlineData("linux", "x86_64", "linux-x64")]
    [InlineData("linux", "arm64", "linux-arm64")]
    [InlineData("linux", "aarch64", "linux-arm64")]
    [InlineData("macos", "arm64", "osx-arm64")]
    [InlineData("macOS", "x64", "osx-x64")]
    [InlineData("osx", "aarch64", "osx-arm64")]
    public void ParseAcceptsCommonNames(string os, string architecture, string runtimeIdentifier)
    {
        Assert.Equal(runtimeIdentifier, TargetPlatform.Parse(os, architecture).RuntimeIdentifier);
    }

    [Fact]
    public void ParseDefaultsToTheHostPlatform()
    {
        // The operating system of the host is the default when Ina can target it; Linux is otherwise.
        var operatingSystem = OperatingSystem.IsMacOS() ? TargetOperatingSystem.MacOS : TargetOperatingSystem.Linux;
        var architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? TargetArchitecture.Arm64 : TargetArchitecture.X64;

        Assert.Equal(new TargetPlatform(operatingSystem, architecture), TargetPlatform.Parse(null, null));
    }

    [Theory]
    [InlineData(TargetOperatingSystem.Linux, "linux", "System.Security.Cryptography.Native.OpenSsl")]
    [InlineData(TargetOperatingSystem.MacOS, "osx", "System.Security.Cryptography.Native.Apple")]
    public void OperatingSystemDeterminesCompilerNameAndCryptographyLibrary(TargetOperatingSystem operatingSystem, string name, string cryptography)
    {
        var target = new TargetPlatform(operatingSystem, TargetArchitecture.X64);

        Assert.Equal(name, target.OperatingSystemName);
        Assert.Equal(cryptography, target.CryptographyLibrary);
    }

    [Theory]
    [InlineData("windows", "x64", "operating system")]
    [InlineData("linux", "riscv64", "architecture")]
    public void ParseRejectsUnsupportedTargetsWithAHelpfulMessage(string os, string architecture, string expected)
    {
        var error = Assert.Throws<InaException>(() => TargetPlatform.Parse(os, architecture));

        Assert.Contains(expected, error.Message);
        Assert.Contains("Supported", error.Message);
    }

    [Fact]
    public void HostRuntimeIdentifierNamesThisMachine()
    {
        var host = TargetPlatform.HostRuntimeIdentifier;

        Assert.Matches("^(linux|linux-musl|win|osx)-(x64|arm64)$", host);
        Assert.Equal(OperatingSystem.IsLinux(), host.StartsWith("linux", StringComparison.Ordinal));
    }
}
