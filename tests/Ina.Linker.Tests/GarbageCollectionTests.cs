using Ina.Linker.Elf;
using Ina.Linker.Tests.Support;

namespace Ina.Linker.Tests;

public class GarbageCollectionTests : ElfLinkerTestBase
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

    [Fact]
    public void ExportedSymbolIsPulledFromAnArchiveEvenIfNothingRefersToIt()
    {
        // Since .NET 11 the runtime library itself holds the descriptor that diagnostic tools look for, in a member
        // that no code references. Exporting the symbol has to load that member, like "-u symbol" does.
        var descriptor = new ElfObjectBuilder();
        descriptor.Global("runtime_descriptor", descriptor.Data(".data", [1, 2, 3, 4]), size: 4, type: SymbolType.Object);
        var archive = ArchiveBuilder.Build(withSymbolTable: true, ("descriptor.o", descriptor.Build()));

        var output = Link(static o => o with { ExportedSymbols = ["runtime_descriptor", "not_defined_anywhere"] }, Code.Function("main", Code.Return(0)), archive);

        // An export that no input defines is not an error: older runtimes name their descriptor differently.
        var image = Load(output);
        Assert.Contains("runtime_descriptor", image.DynamicSymbols);
        Assert.DoesNotContain("not_defined_anywhere", image.DynamicSymbols);
    }

    [Fact]
    public void ExportedSymbolCanBeLookedUpAtRunTime()
    {
        // Diagnostic tools locate the runtime through an exported symbol, so the dynamic symbol table and its hash
        // table must work for definitions too, not only for imports.
        var builder = new ElfObjectBuilder();
        var name = builder.ReadOnlyData(".rodata", "exported_function\0"u8);
        var text = builder.Text(".text",
            0x48, 0x83, 0xec, 0x08,                   // main: sub  rsp, 8
            0x31, 0xff,                               //       xor  edi, edi              ; RTLD_DEFAULT
            0x48, 0x8d, 0x35, 0x00, 0x00, 0x00, 0x00, //       lea  rsi, [rip + name]
            0xe8, 0x00, 0x00, 0x00, 0x00,             //       call dlsym
            0x48, 0x85, 0xc0,                         //       test rax, rax
            0x74, 0x07,                               //       jz   missing
            0xff, 0xd0,                               //       call rax
            0x48, 0x83, 0xc4, 0x08,                   //       add  rsp, 8
            0xc3,                                     //       ret
            0xb8, 0x63, 0x00, 0x00, 0x00,             // missing: mov eax, 99
            0x48, 0x83, 0xc4, 0x08,                   //       add  rsp, 8
            0xc3);                                    //       ret
        builder.Global("main", text);
        builder.Global("exported_function", builder.Text(".text.exported", Code.Return(77)));
        builder.Relocate(text, 9, RelocX64.Pc32, name, -4);
        builder.Relocate(text, 14, RelocX64.Plt32, "dlsym", -4);

        var output = Link(static o => o with { ExportedSymbols = ["exported_function"] }, builder.Build());

        Assert.Equal(77, Run(output).ExitCode);
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
