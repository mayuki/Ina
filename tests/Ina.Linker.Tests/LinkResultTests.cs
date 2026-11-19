namespace Ina.Linker.Tests;

/// <summary>
/// What a link says about the system the executable needs. Both front ends show it in their summary line, so that
/// the reader sees how old a system the executable still runs on.
/// </summary>
public class LinkResultTests
{
    [Fact]
    public void NewestRequiredSymbolVersionIsTheMinimumCLibrary()
    {
        var result = Result("libc.so.6:GLIBC_2.2.5", "libm.so.6:GLIBC_2.17", "libc.so.6:GLIBC_2.14");

        Assert.Equal(new Version(2, 17), result.MinimumGlibcVersion);
        Assert.Null(result.MinimumMacOSVersion);
        Assert.Equal("glibc 2.17+", result.MinimumSystem);
    }

    [Fact]
    public void MacOSImageRequiresAVersionOfTheSystem()
    {
        var result = Result("macOS 12.0");

        Assert.Equal(new Version(12, 0), result.MinimumMacOSVersion);
        Assert.Null(result.MinimumGlibcVersion);
        Assert.Equal("macOS 12.0+", result.MinimumSystem);
    }

    [Fact]
    public void ImageWithoutRequirementsStatesNoMinimumSystem()
    {
        Assert.Null(Result().MinimumSystem);
    }

    private static LinkResult Result(params string[] requiredVersions) => new("app", 0, 0, 0, 0, [], [], requiredVersions);
}
