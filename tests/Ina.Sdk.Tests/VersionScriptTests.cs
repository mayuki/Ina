namespace Ina.Sdk.Tests;

/// <summary>ILCompiler writes the symbols an executable has to export as a version script for GNU-style linkers.</summary>
public class VersionScriptTests
{
    [Fact]
    public void GlobalSymbolsAreRead()
    {
        const string script = """
            V1.0 {
                global:
                    DotNetRuntimeDebugHeader;
                    another_symbol;
                local: *;
            };
            """;

        Assert.Equal(["DotNetRuntimeDebugHeader", "another_symbol"], VersionScript.ReadGlobalSymbols(script));
    }

    [Fact]
    public void ScriptWithoutGlobalSymbolsYieldsNothing()
    {
        Assert.Empty(VersionScript.ReadGlobalSymbols("V1.0 {\n    global:\n    local: *;\n};\n"));
        Assert.Empty(VersionScript.ReadGlobalSymbols("V1.0 {\n    local: *;\n};\n"));
        Assert.Empty(VersionScript.ReadGlobalSymbols(string.Empty));
    }

    [Fact]
    public void LayoutDoesNotMatter()
    {
        Assert.Equal(["a", "b"], VersionScript.ReadGlobalSymbols("V1.0{global:a;b;local:*;};"));
    }

    [Fact]
    public void WildcardsAreRejectedBecauseTheyCannotBeHonored()
    {
        var error = Assert.Throws<FormatException>(() => VersionScript.ReadGlobalSymbols("V1.0 { global: *; };"));

        Assert.Contains("*", error.Message);
    }
}
