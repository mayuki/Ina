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
    [InlineData("windows", "x64", "win-x64")]
    [InlineData("Windows", "amd64", "win-x64")]
    [InlineData("win", "arm64", "win-arm64")]
    public void ParseAcceptsCommonNames(string os, string architecture, string runtimeIdentifier)
    {
        Assert.Equal(runtimeIdentifier, TargetPlatform.Parse(os, architecture).RuntimeIdentifier);
    }

    [Fact]
    public void ParseDefaultsToTheHostWhenItIsASupportedTargetAndToLinuxOtherwise()
    {
        var architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? TargetArchitecture.Arm64 : TargetArchitecture.X64;
        var operatingSystem = OperatingSystem.IsWindows() ? TargetOperatingSystem.Windows : TargetOperatingSystem.Linux;

        Assert.Equal(new TargetPlatform(operatingSystem, architecture), TargetPlatform.Parse(null, null));
    }

    [Fact]
    public void FileNamesFollowTheConventionsOfTheTarget()
    {
        var linux = new TargetPlatform(TargetOperatingSystem.Linux, TargetArchitecture.X64);
        var windows = new TargetPlatform(TargetOperatingSystem.Windows, TargetArchitecture.X64);

        Assert.Equal((string.Empty, ".o"), (linux.ExecutableExtension, linux.ObjectExtension));
        Assert.Equal((".exe", ".obj"), (windows.ExecutableExtension, windows.ObjectExtension));

        // ILCompiler has its own name for Windows.
        Assert.Equal("win", windows.OperatingSystemName);
    }

    [Theory]
    [InlineData("plan9", "x64", "operating system")]
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
