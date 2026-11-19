namespace Ina.Sdk.Tests;

/// <summary>
/// For Apple's linker ILCompiler writes the symbols an executable has to export as a list, one name per line,
/// spelled the way object files spell them: with the underscore in front of a C name.
/// </summary>
public class ExportedSymbolsListTests
{
    [Fact]
    public void SymbolsAreReadInTheirCSpelling()
    {
        Assert.Equal(["DotNetRuntimeDebugHeader", "another_symbol"], ExportedSymbolsList.Read("_DotNetRuntimeDebugHeader\n_another_symbol\n"));
    }

    [Fact]
    public void EmptyLinesCommentsAndSurroundingSpaceAreSkipped()
    {
        Assert.Equal(["a", "b"], ExportedSymbolsList.Read("\n# what the debugger looks up\n  _a  \r\n\n_b"));
        Assert.Empty(ExportedSymbolsList.Read(string.Empty));
    }

    [Fact]
    public void WildcardsAreRejectedBecauseTheyCannotBeHonored()
    {
        var error = Assert.Throws<FormatException>(() => ExportedSymbolsList.Read("_Dot*\n"));

        Assert.Contains("_Dot*", error.Message);
    }

    [Fact]
    public void NameThatNoCSymbolCanHaveIsRejected()
    {
        // The linker takes the names the way C code spells them and cannot express any other symbol.
        var error = Assert.Throws<FormatException>(() => ExportedSymbolsList.Read("DotNetRuntimeDebugHeader\n"));

        Assert.Contains("DotNetRuntimeDebugHeader", error.Message);
    }
}
