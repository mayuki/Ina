using Ina.Linker.Elf;
using Ina.Linker.Tests.Support;

namespace Ina.Linker.Tests;

public class GarbageCollectionTests : LinkerTestBase
{
    [Fact]
    public void UnreachableSectionsAreDiscardedTogetherWithTheirReferences()
    {
        var output = Link(ProgramWithDeadCode());

        var image = Load(output);
        Assert.Contains("main", image.Symbols.Keys);
        Assert.DoesNotContain("dead", image.Symbols.Keys);
        Assert.Equal(42, Run(output).ExitCode);
    }

    [Fact]
    public void WithoutGarbageCollectionDeadCodeMustStillResolve()
    {
        var error = Assert.Throws<LinkerException>(() => Link(static o => o with { GarbageCollectSections = false }, ProgramWithDeadCode()));

        Assert.Contains("undefined symbol: missing", error.Message);
    }

    [Fact]
    public void ExportedSymbolsAreKeptAndListedInTheDynamicSymbolTable()
    {
        var builder = new ElfObjectBuilder();
        builder.Global("main", builder.Text(".text", Code.Return(0)));
        builder.Global("exported_data", builder.Data(".data.exported", [1, 2, 3, 4]), size: 4, type: SymbolType.Object);
        builder.Global("private_data", builder.Data(".data.private", [5, 6, 7, 8]), size: 4, type: SymbolType.Object);

        var output = Link(static o => o with { ExportedSymbols = ["exported_data"] }, builder.Build());

        var image = Load(output);
        Assert.Contains("exported_data", image.DynamicSymbols);
        Assert.DoesNotContain("private_data", image.DynamicSymbols);
        Assert.DoesNotContain("private_data", image.Symbols.Keys);
        Assert.Equal(0, Run(output).ExitCode);
    }

    /// <summary>A program with an unreferenced function that calls a symbol nobody defines.</summary>
    private static byte[] ProgramWithDeadCode()
    {
        var builder = new ElfObjectBuilder();
        builder.Global("main", builder.Text(".text.main", Code.Return(42)));
        var dead = builder.Text(".text.dead", 0xe8, 0x00, 0x00, 0x00, 0x00, 0xc3); // call missing ; ret
        builder.Global("dead", dead);
        builder.Relocate(dead, 1, RelocX64.Plt32, "missing", -4);
        return builder.Build();
    }
}
