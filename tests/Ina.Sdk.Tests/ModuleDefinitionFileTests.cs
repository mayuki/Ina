namespace Ina.Sdk.Tests;

/// <summary>
/// For the Microsoft linker ILCompiler writes the symbols an executable has to export as a module-definition file,
/// which link.exe takes as <c>/DEF</c>.
/// </summary>
public class ModuleDefinitionFileTests
{
    [Fact]
    public void ExportedNamesAreRead()
    {
        // This is what ILCompiler writes for a Windows target.
        const string file = """
            EXPORTS
               DotNetRuntimeContractDescriptor DATA
               another_symbol
            """;

        Assert.Equal(["DotNetRuntimeContractDescriptor", "another_symbol"], ModuleDefinitionFile.ReadExports(file));
    }

    [Fact]
    public void AttributesThatOnlyConcernTheImportLibraryAreAccepted()
    {
        // DATA and PRIVATE change what an import library would say about a name, not what the image exports.
        Assert.Equal(["a", "b", "c"], ModuleDefinitionFile.ReadExports("EXPORTS\r\n a DATA\r\n b PRIVATE\r\n c PRIVATE DATA\r\n"));
    }

    [Fact]
    public void LayoutCommentsAndRepeatedStatementsDoNotMatter()
    {
        const string file = """
            ; what debuggers look up
            EXPORTS a

            EXPORTS
            	b
            ; between definitions
               c
            """;

        Assert.Equal(["a", "b", "c"], ModuleDefinitionFile.ReadExports(file));
        Assert.Empty(ModuleDefinitionFile.ReadExports("EXPORTS\n"));
        Assert.Empty(ModuleDefinitionFile.ReadExports(string.Empty));
    }

    [Theory]
    [InlineData("EXPORTS\n   alias=internal_name\n", "alias=internal_name")]
    [InlineData("EXPORTS\n   alias = internal_name\n", "=")]
    [InlineData("EXPORTS\n   by_ordinal @1\n", "@1")]
    [InlineData("EXPORTS\n   by_ordinal @1 NONAME\n", "@1")]
    public void DefinitionsThatCannotBeHonoredAreRejected(string file, string reported)
    {
        // The linker exports symbols by their own name; renaming and ordinals would be silently lost.
        var error = Assert.Throws<FormatException>(() => ModuleDefinitionFile.ReadExports(file));

        Assert.Contains(reported, error.Message);
    }

    [Theory]
    [InlineData("LIBRARY App\nEXPORTS\n   a\n", "LIBRARY")]
    [InlineData("STACKSIZE 1048576\n", "STACKSIZE")]
    [InlineData("a\n", "'a'")]
    public void OtherStatementsAreRejected(string file, string reported)
    {
        var error = Assert.Throws<FormatException>(() => ModuleDefinitionFile.ReadExports(file));

        Assert.Contains(reported, error.Message);
    }
}
