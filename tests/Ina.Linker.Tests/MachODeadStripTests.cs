using Ina.Linker.MachO;
using Ina.Linker.Tests.Support;

namespace Ina.Linker.Tests;

/// <summary>
/// Dead stripping works on atoms: the pieces of a section between two symbols. What is not reachable from the
/// entry point is left out, together with everything only it refers to.
/// </summary>
public class MachODeadStripTests : MachOLinkerTestBase
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/Versions/A/CoreFoundation";

    [Fact]
    public void UnreachableAtomsAreDiscardedTogetherWithTheirReferences()
    {
        var output = Link(ProgramWithDeadCode());

        var image = Load(output);
        Assert.Contains("_main", image.Symbols.Keys);
        Assert.DoesNotContain("_dead", image.Symbols.Keys);
        Assert.Equal(8UL, image.GetSection("__TEXT", "__text").Size);
        Assert.Equal(42, Run(output).ExitCode);
    }

    [Fact]
    public void WithoutDeadStrippingDeadCodeMustStillResolve()
    {
        var error = Assert.Throws<LinkerException>(() => Link(TargetArchitecture.Arm64, static o => o with { GarbageCollectSections = false }, ProgramWithDeadCode()));

        Assert.Contains("undefined symbol: _missing", error.Message);
    }

    [Fact]
    public void LibraryThatOnlyDeadCodeUsesIsNotADependency()
    {
        var builder = new MachObjectBuilder();
        var text = builder.Text(Code.Arm64(
            0x52800540, Ret,                     // main: mov w0, #42 ; ret
            StpFrame, 0x94000000, LdpFrame, Ret)); // dead: bl _CFRelease
        builder.Global("_main", text);
        builder.Global("_dead", text, value: 8);
        builder.Arm64(text, 12, MachRelocArm64.Branch26, "_CFRelease");

        var image = Load(Link(builder.Build()));

        Assert.DoesNotContain(image.Libraries, static library => library.Name == CoreFoundation);
        Assert.Null(image.FindSection("__TEXT", "__stubs"));
    }

    [Fact]
    public void SymbolMarkedAsNoDeadStripIsKept()
    {
        var builder = new MachObjectBuilder();
        var text = builder.Text(Code.Arm64(0x52800540, Ret, 0x52800020, Ret));
        builder.Global("_main", text);
        builder.Global("_kept", text, value: 8, desc: MachSymbolType.NoDeadStrip);
        var data = builder.Data([1, 2, 3, 4, 5, 6, 7, 8], alignmentLog2: 2);
        builder.Local("_dropped", data);
        builder.Local("_also_kept", data, value: 4, desc: MachSymbolType.NoDeadStrip);

        var image = Load(Link(builder.Build()));

        Assert.Contains("_kept", image.Symbols.Keys);
        Assert.Contains("_also_kept", image.Symbols.Keys);
        Assert.DoesNotContain("_dropped", image.Symbols.Keys);
        Assert.Equal(4UL, image.GetSection("__DATA", "__data").Size);
    }

    [Fact]
    public void SectionMarkedAsNoDeadStripIsKept()
    {
        var builder = new MachObjectBuilder();
        builder.Global("_main", builder.Text(Code.Arm64(0x52800540, Ret)));
        builder.AddSection("__DATA", "__registry", [1, 2, 3, 4], MachSectionType.AttrNoDeadStrip, 2);

        var image = Load(Link(builder.Build()));

        Assert.Equal(4UL, image.GetSection("__DATA", "__registry").Size);
    }

    [Fact]
    public void ObjectWithoutSubsectionsIsKeptSectionBySection()
    {
        // Without the promise that symbols delimit independent pieces, code may fall through from one symbol into
        // the next, so a section can only be kept or dropped as a whole.
        var builder = new MachObjectBuilder { SubsectionsViaSymbols = false };
        var text = builder.Text(Code.Arm64(0x52800540, Ret, 0x52800020, Ret));
        builder.Global("_main", text);
        builder.Global("_also_here", text, value: 8);
        builder.Local("_unused_data", builder.Data([1, 2, 3, 4]));

        var image = Load(Link(builder.Build()));

        Assert.Equal(16UL, image.GetSection("__TEXT", "__text").Size);
        Assert.Contains("_also_here", image.Symbols.Keys);
        Assert.Null(image.FindSection("__DATA", "__data"));
    }

    [Fact]
    public void AlternateEntryPointBelongsToTheAtomInFrontOfIt()
    {
        var builder = new MachObjectBuilder();
        var text = builder.Text(Code.Arm64(0x52800540, Ret, 0x52800020, Ret));
        builder.Global("_main", text);
        builder.Global("_second_entry", text, value: 8, desc: MachSymbolType.AltEntry);

        var image = Load(Link(builder.Build()));

        Assert.Equal(16UL, image.GetSection("__TEXT", "__text").Size);
        Assert.Equal(image.Symbols["_main"] + 8, image.Symbols["_second_entry"]);
    }

    [Fact]
    public void StringLiteralsAreKeptIndividually()
    {
        var builder = new MachObjectBuilder();
        var strings = builder.CString("unused\0used\0never\0"u8);
        builder.Local("l_.str", strings);
        builder.Local("l_.str.1", strings, value: 7);
        var text = builder.Text(Code.Arm64(
            0x90000008,  // adrp x8, used@PAGE
            0x39400100,  // ldrb w0, [x8, used@PAGEOFF]
            Ret));
        builder.Global("_main", text);
        builder.Arm64(text, 0, MachRelocArm64.Page21, "l_.str.1");
        builder.Arm64(text, 4, MachRelocArm64.PageOff12, "l_.str.1");
        var output = Link(builder.Build());

        var image = Load(output);
        Assert.Equal("used\0"u8, image.ReadSection(image.GetSection("__TEXT", "__cstring")));
        Assert.Equal('u', Run(output).ExitCode);
    }

    [Fact]
    public void SectionBoundsAreProvidedAndKeepTheSectionAlive()
    {
        // The .NET runtime finds its tables this way: nothing refers to the entries, only to the bounds.
        var entries = new MachObjectBuilder();
        entries.AddSection("__DATA", "__entries", new byte[24], alignmentLog2: 3);
        var program = new MachObjectBuilder();
        program.AddSection("__DATA", "__entries", new byte[16], alignmentLog2: 3);
        var text = program.Text(Code.Arm64(
            0x90000008,  // adrp x8, start@GOTPAGE
            0xf9400108,  // ldr  x8, [x8, start@GOTPAGEOFF]
            0x90000009,  // adrp x9, end@GOTPAGE
            0xf9400129,  // ldr  x9, [x9, end@GOTPAGEOFF]
            0xcb080120,  // sub  x0, x9, x8
            Ret));
        program.Global("_main", text);
        program.Arm64(text, 0, MachRelocArm64.GotLoadPage21, "section$start$__DATA$__entries");
        program.Arm64(text, 4, MachRelocArm64.GotLoadPageOff12, "section$start$__DATA$__entries");
        program.Arm64(text, 8, MachRelocArm64.GotLoadPage21, "section$end$__DATA$__entries");
        program.Arm64(text, 12, MachRelocArm64.GotLoadPageOff12, "section$end$__DATA$__entries");
        var output = Link(program.Build(), entries.Build());

        var image = Load(output);
        var section = image.GetSection("__DATA", "__entries");
        Assert.Equal(40UL, section.Size);

        // The bounds are addresses of the image itself, so the code computes them without a GOT entry.
        Assert.Null(image.FindSection("__DATA_CONST", "__got"));
        var main = image.Symbols["_main"];
        Assert.Equal(section.Address, AdrpTarget(image, main) + Imm12(image, main + 4));
        Assert.Equal(section.Address + section.Size, AdrpTarget(image, main + 8) + Imm12(image, main + 12));
        Assert.Equal(40, Run(output).ExitCode);
    }

    [Fact]
    public void BoundsOfASectionThatDoesNotExistAreEqual()
    {
        var builder = new MachObjectBuilder();
        var text = builder.Text(Code.Arm64(
            0x90000008, 0xf9400108, 0x90000009, 0xf9400129,
            0xcb080120,  // sub x0, x9, x8
            Ret));
        builder.Global("_main", text);
        builder.Arm64(text, 0, MachRelocArm64.GotLoadPage21, "section$start$__DATA$__nothing");
        builder.Arm64(text, 4, MachRelocArm64.GotLoadPageOff12, "section$start$__DATA$__nothing");
        builder.Arm64(text, 8, MachRelocArm64.GotLoadPage21, "section$end$__DATA$__nothing");
        builder.Arm64(text, 12, MachRelocArm64.GotLoadPageOff12, "section$end$__DATA$__nothing");

        Assert.Equal(0, Run(Link(builder.Build())).ExitCode);
    }

    /// <summary>A program with a function that nothing calls and that refers to a symbol that does not exist.</summary>
    private static byte[] ProgramWithDeadCode()
    {
        var builder = new MachObjectBuilder();
        var text = builder.Text(Code.Arm64(
            0x52800540, Ret,                       // main: mov w0, #42 ; ret
            StpFrame, 0x94000000, LdpFrame, Ret)); // dead: bl _missing
        builder.Global("_main", text);
        builder.Global("_dead", text, value: 8);
        builder.Arm64(text, 12, MachRelocArm64.Branch26, "_missing");
        return builder.Build();
    }
}
