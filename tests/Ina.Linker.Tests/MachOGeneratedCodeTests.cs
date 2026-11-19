using Ina.Linker.MachO;
using Ina.Linker.Tests.Support;

namespace Ina.Linker.Tests;

/// <summary>
/// Code that Apple's toolchain adds to a link by itself and that Ina's linker therefore has to generate: stubs for
/// Objective-C message sends and functions of the compiler support library.
/// </summary>
public class MachOGeneratedCodeTests : MachOLinkerTestBase
{
    [Fact]
    public void ObjectiveCSelectorStubLoadsTheSelectorAndSendsTheMessage()
    {
        var output = Link(MessageSendProgram());

        // The stub: adrp x1, selector@PAGE ; ldr x1, [x1, selector@PAGEOFF] ; adrp x16, got ; ldr x16, [x16, got] ; br x16
        var image = Load(output);
        var stubs = image.GetSection("__TEXT", "__objc_stubs");
        var selectors = image.GetSection("__DATA", "__objc_selrefs");
        var stub = BranchTarget(image, image.Symbols["_main"] + 24);
        Assert.Equal(stubs.Address, stub);
        Assert.Equal(selectors.Address, AdrpTarget(image, stub) + Imm12(image, stub + 4) * 8);
        var slot = AdrpTarget(image, stub + 8) + Imm12(image, stub + 12) * 8;
        Assert.Contains(image.Binds, bind => bind.Address == slot && bind.SymbolName == "_objc_msgSend");

        // The reference points at the name of the selector until the Objective-C runtime registers it.
        Assert.Contains(selectors.Address, image.Rebases);
        var names = image.GetSection("__TEXT", "__objc_methname");
        Assert.Equal(names.Address, image.U64(selectors.Address));
        Assert.Equal("class\0"u8, image.ReadSection(names));
        Assert.Equal(1, Run(output).ExitCode);
    }

    [Fact]
    public void SelectorStubIsSharedAndOnlyGeneratedWhenUsed()
    {
        var caller = new MachObjectBuilder();
        var text = caller.Text(Code.Arm64(StpFrame, 0x94000000, LdpFrame, Ret));
        caller.Global("_again", text, desc: MachSymbolType.NoDeadStrip);
        caller.Arm64(text, 4, MachRelocArm64.Branch26, "_objc_msgSend$class");
        var unused = new MachObjectBuilder();
        var dead = unused.Text(Code.Arm64(StpFrame, 0x94000000, LdpFrame, Ret));
        unused.Global("_never_called", dead);
        unused.Arm64(dead, 4, MachRelocArm64.Branch26, "_objc_msgSend$description");

        var image = Load(Link(MessageSendProgram(), caller.Build(), unused.Build()));

        Assert.Equal(20UL, image.GetSection("__TEXT", "__objc_stubs").Size);
        Assert.Equal(8UL, image.GetSection("__DATA", "__objc_selrefs").Size);
        Assert.Equal("class\0"u8, image.ReadSection(image.GetSection("__TEXT", "__objc_methname")));
    }

    [Fact]
    public void PlatformVersionCheckAsksTheCLibrary()
    {
        var builder = new MachObjectBuilder();
        var text = builder.Text(Code.Arm64(
            0xa9be7bfd,  // stp  x29, x30, [sp, #-32]!
            0xf9000bf3,  // str  x19, [sp, #16]
            0x52800020,  // mov  w0, #1                 ; platform: macOS
            0x52800141,  // mov  w1, #10                ; 10.0.0, which every Mac that runs this satisfies
            0x52800002,  // mov  w2, #0
            0x52800003,  // mov  w3, #0
            0x94000000,  // bl   ___isPlatformVersionAtLeast
            0x531f7813,  // lsl  w19, w0, #1
            0x52800020,  // mov  w0, #1
            0x52807ce1,  // mov  w1, #999               ; 999.0.0, which none does
            0x52800002,  // mov  w2, #0
            0x52800003,  // mov  w3, #0
            0x94000000,  // bl   ___isPlatformVersionAtLeast
            0x0b000260,  // add  w0, w19, w0
            0xf9400bf3,  // ldr  x19, [sp, #16]
            0xa8c27bfd,  // ldp  x29, x30, [sp], #32
            Ret));
        builder.Global("_main", text);
        builder.Arm64(text, 24, MachRelocArm64.Branch26, "___isPlatformVersionAtLeast");
        builder.Arm64(text, 48, MachRelocArm64.Branch26, "___isPlatformVersionAtLeast");
        var output = Link(builder.Build());

        // What the compiler support library would contribute is generated and calls into the C library.
        var image = Load(output);
        Assert.Contains("__availability_version_check", image.UndefinedSymbols);
        Assert.Equal(2, Run(output).ExitCode);
    }

    [Fact]
    public void OlderVersionCheckImpliesThePlatform()
    {
        var builder = new MachObjectBuilder();
        var text = builder.Text(Code.Arm64(
            StpFrame,
            0x52800140,  // mov w0, #10
            0x528001e1,  // mov w1, #15
            0x52800002,  // mov w2, #0
            0x94000000,  // bl  ___isOSVersionAtLeast
            LdpFrame,
            Ret));
        builder.Global("_main", text);
        builder.Arm64(text, 16, MachRelocArm64.Branch26, "___isOSVersionAtLeast");

        Assert.Equal(1, Run(Link(builder.Build())).ExitCode);
    }

    [Fact]
    public void InputThatDefinesASupportFunctionItselfWins()
    {
        var own = Function("___isPlatformVersionAtLeast", 0x528000e0, Ret); // mov w0, #7

        var output = Link(MainCalling("___isPlatformVersionAtLeast"), own);

        Assert.DoesNotContain("__availability_version_check", Load(output).UndefinedSymbols);
        Assert.Equal(7, Run(output).ExitCode);
    }

    /// <summary>A program that returns 1 if <c>[NSObject class]</c>, sent through a selector stub, yields the class.</summary>
    private static byte[] MessageSendProgram()
    {
        var builder = new MachObjectBuilder();
        var name = builder.CString("NSObject\0"u8);
        builder.Local("l_.str", name);

        // Marks the object as containing Objective-C code, which makes the runtime look at its selector references.
        builder.AddSection("__DATA", "__objc_imageinfo", new byte[8], MachSectionType.AttrNoDeadStrip, 2);
        var text = builder.Text(Code.Arm64(
            0xa9be7bfd,  // stp  x29, x30, [sp, #-32]!
            0xf9000bf3,  // str  x19, [sp, #16]
            0x90000000,  // adrp x0, name@PAGE
            0x91000000,  // add  x0, x0, name@PAGEOFF
            0x94000000,  // bl   _objc_getClass
            0xaa0003f3,  // mov  x19, x0
            0x94000000,  // bl   _objc_msgSend$class
            0xeb13001f,  // cmp  x0, x19
            0x1a9f17e0,  // cset w0, eq
            0xf9400bf3,  // ldr  x19, [sp, #16]
            0xa8c27bfd,  // ldp  x29, x30, [sp], #32
            Ret));
        builder.Global("_main", text);
        builder.Arm64(text, 8, MachRelocArm64.Page21, "l_.str");
        builder.Arm64(text, 12, MachRelocArm64.PageOff12, "l_.str");
        builder.Arm64(text, 16, MachRelocArm64.Branch26, "_objc_getClass");
        builder.Arm64(text, 24, MachRelocArm64.Branch26, "_objc_msgSend$class");
        return builder.Build();
    }
}
