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
    public void ParseAcceptsCommonNames(string os, string architecture, string runtimeIdentifier)
    {
        Assert.Equal(runtimeIdentifier, TargetPlatform.Parse(os, architecture).RuntimeIdentifier);
    }

    [Fact]
    public void ParseDefaultsToLinuxOnTheHostArchitecture()
    {
        var expected = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? TargetArchitecture.Arm64 : TargetArchitecture.X64;

        Assert.Equal(new TargetPlatform(TargetOperatingSystem.Linux, expected), TargetPlatform.Parse(null, null));
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
