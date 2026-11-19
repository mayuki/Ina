using Ina.Linker.MachO;
using Ina.Linker.Tests.Support;

namespace Ina.Linker.Tests;

public class MachOSymbolResolutionTests : MachOLinkerTestBase
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/Versions/A/CoreFoundation";

    [Fact]
    public void PullsArchiveMembersThatDefineUndefinedSymbols()
    {
        // The second member refers to a symbol that exists nowhere; it must not be loaded because nothing needs it.
        var archive = MachArchiveBuilder.Build(
            withSymbolTable: true,
            ("helper.o", Function("_helper", 0x528000a0, Ret)), // mov w0, #5
            ("a_member_with_a_rather_long_name.o", CallerOf("_unused", "_does_not_exist")));

        Assert.Equal(5, Run(Link(MainCalling("_helper"), archive)).ExitCode);
    }

    [Fact]
    public void ArchiveMembersCanPullInFurtherMembers()
    {
        var archive = MachArchiveBuilder.Build(
            withSymbolTable: true,
            ("leaf.o", Function("_leaf", 0x528000c0, Ret)), // mov w0, #6
            ("middle.o", CallerOf("_middle", "_leaf")));

        Assert.Equal(6, Run(Link(MainCalling("_middle"), archive)).ExitCode);
    }

    [Fact]
    public void ArchiveWithoutSymbolTableIsIndexedFromItsMembers()
    {
        var archive = MachArchiveBuilder.Build(withSymbolTable: false, ("helper.o", Function("_helper", 0x52800100, Ret))); // mov w0, #8

        Assert.Equal(8, Run(Link(MainCalling("_helper"), archive)).ExitCode);
    }

    [Fact]
    public void StrongDefinitionReplacesWeakOne()
    {
        var weak = new MachObjectBuilder();
        weak.WeakDefinition("_value", weak.Text(Code.Arm64(0x52800020, Ret))); // mov w0, #1

        var output = Link(MainCalling("_value"), weak.Build(), Function("_value", 0x52800040, Ret)); // mov w0, #2

        Assert.Equal(2, Run(output).ExitCode);
    }

    [Fact]
    public void FirstWeakDefinitionIsUsedWhenNothingElseDefinesTheSymbol()
    {
        // C++ emits inline functions as weak definitions into every object file that uses them.
        var first = new MachObjectBuilder();
        first.WeakDefinition("_value", first.Text(Code.Arm64(0x52800020, Ret))); // mov w0, #1
        var second = new MachObjectBuilder();
        second.WeakDefinition("_value", second.Text(Code.Arm64(0x52800040, Ret))); // mov w0, #2

        Assert.Equal(1, Run(Link(MainCalling("_value"), first.Build(), second.Build())).ExitCode);
    }

    [Fact]
    public void PrivateExternalSymbolIsVisibleToOtherObjectFiles()
    {
        var hidden = new MachObjectBuilder();
        hidden.PrivateExternal("_helper", hidden.Text(Code.Arm64(0x52800060, Ret))); // mov w0, #3

        Assert.Equal(3, Run(Link(MainCalling("_helper"), hidden.Build())).ExitCode);
    }

    [Fact]
    public void DuplicateStrongDefinitionsAreReported()
    {
        var error = Assert.Throws<LinkerException>(() => Link(
            Function("_main", 0x52800000, Ret), Function("_twice", 0x52800020, Ret), Function("_twice", 0x52800040, Ret)));

        Assert.Contains("duplicate symbol: _twice", error.Message);
        Assert.Contains("input1", error.Message);
        Assert.Contains("input2", error.Message);
    }

    [Fact]
    public void UndefinedSymbolIsReportedWithTheReferringSection()
    {
        var error = Assert.Throws<LinkerException>(() => Link(MainCalling("_does_not_exist")));

        Assert.Contains("undefined symbol: _does_not_exist", error.Message);
        Assert.Contains("input0:(__TEXT,__text)", error.Message);
        Assert.DoesNotContain("weakly", error.Message);
    }

    [Fact]
    public void MissingEntryPointIsReported()
    {
        var error = Assert.Throws<LinkerException>(() => Link(Function("_not_main", 0x52800000, Ret)));

        Assert.Contains("undefined symbol: _main", error.Message);
    }

    [Fact]
    public void UnresolvedWeakReferenceEvaluatesToZero()
    {
        var output = Link(AddressIsNullProgram("_not_available_anywhere"));

        Assert.Empty(Load(output).Binds);
        Assert.Equal(1, Run(output).ExitCode);
    }

    [Fact]
    public void WeakReferenceToALibrarySymbolMayBeMissingAtRunTime()
    {
        var image = Load(Link(AddressIsNullProgram("_CFRelease")));

        // The loader stores null instead of failing when the symbol or even the whole library does not exist.
        var bind = Assert.Single(image.Binds);
        Assert.Equal("_CFRelease", bind.SymbolName);
        Assert.True(bind.IsWeakImport);
        Assert.Equal(CoreFoundation, image.LibraryOf(bind));
        Assert.True(image.Libraries.Single(static library => library.Name == CoreFoundation).IsWeak);
        var symbol = image.SymbolTable.Single(static symbol => symbol.Name == "_CFRelease");
        Assert.NotEqual(0, symbol.Desc & MachSymbolType.WeakReference);
    }

    [Fact]
    public void SymbolThatIsAlsoReferencedStronglyIsRequired()
    {
        var image = Load(Link(AddressIsNullProgram("_CFRelease"), CallerOf("_other", "_CFRelease"), CallerOf("_keep", "_other")));

        // Only the weak reference is live here, but a strong one exists in the inputs: err on the side of requiring it.
        Assert.False(Assert.Single(image.Binds).IsWeakImport);
        Assert.False(image.Libraries.Single(static library => library.Name == CoreFoundation).IsWeak);
    }

    [Fact]
    public void CommonSymbolsGetZeroFilledStorageOfTheLargestSize()
    {
        var first = new MachObjectBuilder();
        first.Common("_shared", 4, 2);
        first.Global("_unrelated", first.Text(Code.Arm64(Ret)));
        var second = new MachObjectBuilder();
        second.Common("_shared", 8, 3);
        var text = second.Text(Code.Arm64(
            0x90000008,  // adrp x8, _shared@GOTPAGE
            0xf9400108,  // ldr  x8, [x8, _shared@GOTPAGEOFF]
            0x52800129,  // mov  w9, #9
            0xb9000509,  // str  w9, [x8, #4]
            0xb9400500,  // ldr  w0, [x8, #4]
            Ret));
        second.Global("_main", text);
        second.Arm64(text, 0, MachRelocArm64.GotLoadPage21, "_shared");
        second.Arm64(text, 4, MachRelocArm64.GotLoadPageOff12, "_shared");
        var output = Link(first.Build(), second.Build());

        var image = Load(output);
        var common = image.GetSection("__DATA", "__common");
        Assert.Equal(MachSectionType.ZeroFill, common.Type);
        Assert.Equal(8UL, common.Size);
        Assert.Equal(9, Run(output).ExitCode);
    }

    [Fact]
    public void DefinitionReplacesCommonSymbol()
    {
        var tentative = new MachObjectBuilder();
        tentative.Common("_value", 4, 2);
        tentative.Global("_unrelated", tentative.Text(Code.Arm64(Ret)));
        var definition = new MachObjectBuilder();
        definition.Global("_value", definition.Data([0x21, 0, 0, 0]));
        var program = new MachObjectBuilder();
        var text = program.Text(Code.Arm64(
            0x90000008,  // adrp x8, _value@GOTPAGE
            0xf9400108,  // ldr  x8, [x8, _value@GOTPAGEOFF]
            0xb9400100,  // ldr  w0, [x8]
            Ret));
        program.Global("_main", text);
        program.Arm64(text, 0, MachRelocArm64.GotLoadPage21, "_value");
        program.Arm64(text, 4, MachRelocArm64.GotLoadPageOff12, "_value");
        var output = Link(tentative.Build(), program.Build(), definition.Build());

        Assert.Null(Load(output).FindSection("__DATA", "__common"));
        Assert.Equal(0x21, Run(output).ExitCode);
    }

    [Fact]
    public void ObjectForAnotherArchitectureIsRejected()
    {
        var intel = new MachObjectBuilder(TargetArchitecture.X64);
        intel.Global("_helper", intel.Text(0xc3));

        var error = Assert.Throws<LinkerException>(() => Link(MainCalling("_helper"), intel.Build()));

        Assert.Contains("different architecture", error.Message);
    }

    [Fact]
    public void UnknownInputFormatIsRejected()
    {
        var error = Assert.Throws<LinkerException>(() => Link(Function("_main", 0x52800000, Ret), "not an object file"u8.ToArray()));

        Assert.Contains("unknown file format", error.Message);
    }

    [Fact]
    public void DataOfALibraryCanNotBeAddressedDirectly()
    {
        // Code has to load the address of a library symbol from the GOT; computing it is only possible inside the image.
        var builder = new MachObjectBuilder();
        var text = builder.Text(Code.Arm64(0x90000008, 0xb9400100, Ret)); // adrp x8, sym@PAGE ; ldr w0, [x8, sym@PAGEOFF]
        builder.Global("_main", text);
        builder.Arm64(text, 0, MachRelocArm64.Page21, "___stdoutp");
        builder.Arm64(text, 4, MachRelocArm64.PageOff12, "___stdoutp");

        var error = Assert.Throws<LinkerException>(() => Link(builder.Build()));

        Assert.Contains("direct reference to '___stdoutp'", error.Message);
        Assert.Contains("libSystem", error.Message);
    }

    /// <summary>A program that returns 1 if the address of a weakly referenced symbol is null and 0 otherwise.</summary>
    private static byte[] AddressIsNullProgram(string symbol)
    {
        var builder = new MachObjectBuilder();
        builder.WeakReference(symbol);
        var text = builder.Text(Code.Arm64(
            0x90000008,  // adrp x8, symbol@GOTPAGE
            0xf9400108,  // ldr  x8, [x8, symbol@GOTPAGEOFF]
            0xf100011f,  // cmp  x8, #0
            0x1a9f17e0,  // cset w0, eq
            Ret));
        builder.Global("_main", text);
        builder.Arm64(text, 0, MachRelocArm64.GotLoadPage21, symbol);
        builder.Arm64(text, 4, MachRelocArm64.GotLoadPageOff12, symbol);
        return builder.Build();
    }
}
