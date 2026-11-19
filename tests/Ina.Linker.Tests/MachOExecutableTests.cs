using Ina.Linker.MachO;
using Ina.Linker.Tests.Support;

namespace Ina.Linker.Tests;

/// <summary>Links small programs for macOS and runs them: the linked image has to work, not just look right.</summary>
public class MachOExecutableTests : MachOLinkerTestBase
{
    [Fact]
    public void ReturnValueOfMainBecomesTheExitCode()
    {
        var output = Link(Function("_main", 0x52800540, Ret)); // mov w0, #42 ; ret

        Assert.Equal(42, Run(output).ExitCode);
    }

    [Fact]
    public void CallsImportedFunctionThroughStub()
    {
        var (exitCode, output) = Run(Link(PrintMessageProgram()));

        Assert.Equal(0, exitCode);
        Assert.Equal("hello from ina\n", output);
    }

    [Fact]
    public void ReadsImportedDataThroughGlobalOffsetTable()
    {
        var builder = new MachObjectBuilder();
        var message = builder.CString("via got\n\0"u8);
        builder.Local("l_.str", message);
        var text = builder.Text(Code.Arm64(
            StpFrame,
            0x90000001,  // adrp x1, ___stdoutp@GOTPAGE
            0xf9400021,  // ldr  x1, [x1, ___stdoutp@GOTPAGEOFF]
            0xf9400021,  // ldr  x1, [x1]
            0x90000000,  // adrp x0, message@PAGE
            0x91000000,  // add  x0, x0, message@PAGEOFF
            0x94000000,  // bl   _fputs
            0x52800000,  // mov  w0, #0
            LdpFrame,
            Ret));
        builder.Global("_main", text);
        builder.Arm64(text, 4, MachRelocArm64.GotLoadPage21, "___stdoutp");
        builder.Arm64(text, 8, MachRelocArm64.GotLoadPageOff12, "___stdoutp");
        builder.Arm64(text, 16, MachRelocArm64.Page21, "l_.str");
        builder.Arm64(text, 20, MachRelocArm64.PageOff12, "l_.str");
        builder.Arm64(text, 24, MachRelocArm64.Branch26, "_fputs");

        var (exitCode, output) = Run(Link(builder.Build()));

        Assert.Equal(0, exitCode);
        Assert.Equal("via got\n", output);
    }

    [Fact]
    public void RunsInitializersBeforeMain()
    {
        var builder = new MachObjectBuilder();
        var counter = builder.ZeroFill("__bss", 4);
        builder.Local("_counter", counter);
        var text = builder.Text(Code.Arm64(
            0x90000008,  // ctor: adrp x8, _counter@PAGE
            0x528000e9,  //       mov  w9, #7
            0xb9000109,  //       str  w9, [x8, _counter@PAGEOFF]
            Ret,
            0x90000008,  // main: adrp x8, _counter@PAGE
            0xb9400100,  //       ldr  w0, [x8, _counter@PAGEOFF]
            Ret));
        builder.Local("_ctor", text);
        builder.Global("_main", text, value: 16);
        builder.Arm64(text, 0, MachRelocArm64.Page21, "_counter");
        builder.Arm64(text, 8, MachRelocArm64.PageOff12, "_counter");
        builder.Arm64(text, 16, MachRelocArm64.Page21, "_counter");
        builder.Arm64(text, 20, MachRelocArm64.PageOff12, "_counter");
        var initializers = builder.AddSection("__DATA", "__mod_init_func", new byte[8], MachSectionType.ModInitFuncPointers, 3);
        builder.Pointer(initializers, 0, "_ctor");

        Assert.Equal(7, Run(Link(builder.Build())).ExitCode);
    }

    [Fact]
    public void AbsoluteAddressesAreRebasedWhenTheImageMoves()
    {
        // The pointer stored in __data has to be adjusted by the loader, because the image is loaded at a random address.
        var output = Link(PointerTableProgram());

        var image = Load(output);
        var table = image.Symbols["_table"];
        Assert.Contains(table, image.Rebases);
        Assert.Equal("pointer\0"u8, image.ReadAt(image.U64(table), 8));
        Assert.Equal((0, "pointer\n"), Run(output));
    }

    [Fact]
    public void ThreadLocalVariableIsReachedThroughItsDescriptor()
    {
        var builder = new MachObjectBuilder();
        var initialValue = builder.AddSection("__DATA", "__thread_data", [0x15, 0, 0, 0], MachSectionType.ThreadLocalRegular, 2);
        builder.Local("_value$tlv$init", initialValue);

        // A descriptor is three words: the function that returns the address, a key the loader assigns, and the
        // offset of the variable inside the per-thread block.
        var descriptor = builder.AddSection("__DATA", "__thread_vars", new byte[24], MachSectionType.ThreadLocalVariables, 3);
        builder.Global("_value", descriptor);
        builder.Pointer(descriptor, 0, "__tlv_bootstrap");
        builder.Pointer(descriptor, 16, "_value$tlv$init");

        var text = builder.Text(Code.Arm64(
            StpFrame,
            0x90000000,  // adrp x0, _value@TLVPPAGE
            0xf9400000,  // ldr  x0, [x0, _value@TLVPPAGEOFF]
            0xf9400008,  // ldr  x8, [x0]
            0xd63f0100,  // blr  x8
            0xb9400000,  // ldr  w0, [x0]
            LdpFrame,
            Ret));
        builder.Global("_main", text);
        builder.Arm64(text, 4, MachRelocArm64.TlvpLoadPage21, "_value");
        builder.Arm64(text, 8, MachRelocArm64.TlvpLoadPageOff12, "_value");
        var output = Link(builder.Build());

        var image = Load(output);
        Assert.NotEqual(0u, image.Flags & MachHeader.HasTlvDescriptors);
        var variable = image.Symbols["_value"];
        Assert.Contains(image.Binds, bind => bind.Address == variable && bind.SymbolName == "__tlv_bootstrap");
        Assert.Equal(0UL, image.U64(variable + 16));
        Assert.DoesNotContain(variable + 16, image.Rebases);

        // The code computes the address of the descriptor instead of loading it from a pointer.
        var main = image.Symbols["_main"];
        Assert.Equal(variable, AdrpTarget(image, main + 4) + Imm12(image, main + 8));
        Assert.Equal(0x91000000u, image.U32(main + 8) & 0xffc00000);
        Assert.Equal(0x15, Run(output).ExitCode);
    }

    [Fact]
    public void ThreadLocalVariablesWithAndWithoutInitialValueShareOneTemplate()
    {
        var builder = new MachObjectBuilder();
        var initialValue = builder.AddSection("__DATA", "__thread_data", [9, 0, 0, 0], MachSectionType.ThreadLocalRegular, 2);
        builder.Local("_first$tlv$init", initialValue);
        var zeroed = builder.ZeroFill("__thread_bss", 8, MachSectionType.ThreadLocalZeroFill);
        builder.Local("_second$tlv$init", zeroed);
        var descriptors = builder.AddSection("__DATA", "__thread_vars", new byte[48], MachSectionType.ThreadLocalVariables, 3);
        builder.Global("_first", descriptors);
        builder.Global("_second", descriptors, value: 24);
        builder.Pointer(descriptors, 0, "__tlv_bootstrap");
        builder.Pointer(descriptors, 16, "_first$tlv$init");
        builder.Pointer(descriptors, 24, "__tlv_bootstrap");
        builder.Pointer(descriptors, 40, "_second$tlv$init");
        builder.Global("_main", builder.Text(Code.Arm64(0x52800000, Ret)));

        var image = Load(Link(TargetArchitecture.Arm64, static o => o with { GarbageCollectSections = false }, builder.Build()));

        // The loader copies everything from the first thread-local data section to the end of the last one.
        var data = image.GetSection("__DATA", "__thread_data");
        var bss = image.GetSection("__DATA", "__thread_bss");
        Assert.True(data.Address < bss.Address);
        Assert.Equal(0UL, image.U64(image.Symbols["_first"] + 16));
        Assert.Equal(bss.Address - data.Address, image.U64(image.Symbols["_second"] + 16));
    }

    [Fact]
    public void ExportedSymbolIsFoundByDlsym()
    {
        var builder = new MachObjectBuilder();
        var name = builder.CString("answer\0"u8);
        builder.Local("l_.str", name);
        var data = builder.Data([0x2a, 0, 0, 0]);
        builder.Global("_answer", data);
        var text = builder.Text(Code.Arm64(
            StpFrame,
            0x92800020,  // mov  x0, #-2          ; RTLD_DEFAULT
            0x90000001,  // adrp x1, name@PAGE
            0x91000021,  // add  x1, x1, name@PAGEOFF
            0x94000000,  // bl   _dlsym
            0xb9400000,  // ldr  w0, [x0]
            LdpFrame,
            Ret));
        builder.Global("_main", text);
        builder.Arm64(text, 8, MachRelocArm64.Page21, "l_.str");
        builder.Arm64(text, 12, MachRelocArm64.PageOff12, "l_.str");
        builder.Arm64(text, 16, MachRelocArm64.Branch26, "_dlsym");
        var output = Link(TargetArchitecture.Arm64, static o => o with { ExportedSymbols = ["answer"] }, builder.Build());

        var image = Load(output);
        Assert.Equal(image.Symbols["_answer"], image.Exports["_answer"]);
        Assert.Equal(42, Run(output).ExitCode);
    }

    [Fact]
    public void DifferenceOfTwoSymbolsIsComputedAtLinkTime()
    {
        var builder = new MachObjectBuilder();
        var text = builder.Text(Code.Arm64(
            0x90000008,  // main:  adrp x8, _distance@PAGE
            0xb9400100,  //        ldr  w0, [x8, _distance@PAGEOFF]
            Ret,
            Ret));       // other: ret
        builder.Global("_main", text);
        builder.Local("_other", text, value: 12);
        var data = builder.Const([3, 0, 0, 0], alignmentLog2: 2);
        builder.Local("_distance", data);
        builder.Arm64(text, 0, MachRelocArm64.Page21, "_distance");
        builder.Arm64(text, 4, MachRelocArm64.PageOff12, "_distance");
        builder.Difference(data, 0, "_other", "_main"); // the field already holds 3, which is added
        var output = Link(builder.Build());

        var image = Load(output);
        Assert.Equal(15u, image.U32(image.Symbols["_distance"]));
        Assert.Empty(image.Rebases);
        Assert.Equal(15, Run(output).ExitCode);
    }

    [Fact]
    public void ReferenceWithAddendAddressesTheMiddleOfAnAtom()
    {
        var builder = new MachObjectBuilder();
        var data = builder.Data([1, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0], alignmentLog2: 2);
        builder.Local("_values", data);
        var text = builder.Text(Code.Arm64(
            0x90000008,  // adrp x8, _values@PAGE + 8
            0xb9400100,  // ldr  w0, [x8, _values@PAGEOFF + 8]
            Ret));
        builder.Global("_main", text);
        builder.Arm64(text, 0, MachRelocArm64.Page21, "_values", addend: 8);
        builder.Arm64(text, 4, MachRelocArm64.PageOff12, "_values", addend: 8);

        Assert.Equal(3, Run(Link(builder.Build())).ExitCode);
    }

    [Fact]
    public void RelativePointerToAGotEntryIgnoresWhatTheFieldContains()
    {
        // Swift metadata refers to types of other images this way: a 32-bit distance to a GOT entry. Assemblers
        // leave a meaningless value in such a field, which is not an addend.
        var builder = new MachObjectBuilder();
        builder.Global("_value", builder.Data([0x17, 0, 0, 0]));
        var reference = builder.Const([7, 0, 0, 0, 0xfc, 0xff, 0xff, 0xbf], alignmentLog2: 2);
        builder.Local("_reference", reference, value: 4);
        builder.Relocate(reference, 4, MachRelocArm64.PointerToGot, "_value", pcRel: true);
        var text = builder.Text(Code.Arm64(
            0x90000008,  // adrp  x8, _reference@PAGE
            0x91000108,  // add   x8, x8, _reference@PAGEOFF
            0xb9800109,  // ldrsw x9, [x8]
            0x8b090108,  // add   x8, x8, x9
            0xf9400108,  // ldr   x8, [x8]
            0xb9400100,  // ldr   w0, [x8]
            Ret));
        builder.Global("_main", text);
        builder.Arm64(text, 0, MachRelocArm64.Page21, "_reference");
        builder.Arm64(text, 4, MachRelocArm64.PageOff12, "_reference");
        var output = Link(builder.Build());

        // The entry holds an address of the image, which the loader adjusts.
        var image = Load(output);
        var field = image.Symbols["_reference"];
        var slot = field + (ulong)(long)(int)image.U32(field);
        Assert.Equal(image.GetSection("__DATA_CONST", "__got").Address, slot);
        Assert.Equal(image.Symbols["_value"], image.U64(slot));
        Assert.Equal([slot], image.Rebases);
        Assert.Equal(0x17, Run(output).ExitCode);
    }

    /// <summary>A program that prints a message with <c>puts</c>.</summary>
    internal static byte[] PrintMessageProgram(string message = "hello from ina")
    {
        var builder = new MachObjectBuilder();
        var text = builder.Text(Code.Arm64(
            StpFrame,
            0x90000000,  // adrp x0, message@PAGE
            0x91000000,  // add  x0, x0, message@PAGEOFF
            0x94000000,  // bl   _puts
            0x52800000,  // mov  w0, #0
            LdpFrame,
            Ret));
        var cstring = builder.CString(System.Text.Encoding.UTF8.GetBytes(message + "\0"));
        builder.Local("l_.str", cstring);
        builder.Global("_main", text);
        builder.Arm64(text, 4, MachRelocArm64.Page21, "l_.str");
        builder.Arm64(text, 8, MachRelocArm64.PageOff12, "l_.str");
        builder.Arm64(text, 12, MachRelocArm64.Branch26, "_puts");
        return builder.Build();
    }

    /// <summary>A program that prints the string that a pointer in its data section refers to.</summary>
    private static byte[] PointerTableProgram()
    {
        var builder = new MachObjectBuilder();
        var message = builder.CString("pointer\0"u8);
        var table = builder.Data(new byte[8]);
        builder.Local("_table", table);
        builder.Pointer(table, 0, message);
        var text = builder.Text(Code.Arm64(
            StpFrame,
            0x90000008,  // adrp x8, _table@PAGE
            0xf9400100,  // ldr  x0, [x8, _table@PAGEOFF]
            0x94000000,  // bl   _puts
            0x52800000,  // mov  w0, #0
            LdpFrame,
            Ret));
        builder.Global("_main", text);
        builder.Arm64(text, 4, MachRelocArm64.Page21, "_table");
        builder.Arm64(text, 8, MachRelocArm64.PageOff12, "_table");
        builder.Arm64(text, 12, MachRelocArm64.Branch26, "_puts");
        return builder.Build();
    }
}
