using Ina.Linker.Elf;
using Ina.Linker.Tests.Support;

namespace Ina.Linker.Tests;

/// <summary>
/// AArch64 support. The encoding of every relocation is checked by decoding the patched instructions, which works
/// on any host; in addition the programs are executed when an AArch64 machine or emulator is available.
/// </summary>
public class Arm64Tests : LinkerTestBase
{
    private const uint StpFrame = 0xa9bf7bfd; // stp x29, x30, [sp, #-16]!
    private const uint LdpFrame = 0xa8c17bfd; // ldp x29, x30, [sp], #16
    private const uint Ret = 0xd65f03c0;
    private const uint Nop = 0xd503201f;

    [Fact]
    public void ImageTargetsAArch64WithItsLoaderAndPageSize()
    {
        var output = LinkArm64(Function("main", 0x52800540, Ret)); // mov w0, #42 ; ret
        var image = Load(output);

        Assert.Equal((ushort)ElfMachine.AArch64, image.Machine);
        Assert.Equal("/lib/ld-linux-aarch64.so.1", image.Interpreter);
        Assert.Contains("libc.so.6", image.Needed);

        // 64 KiB alignment keeps the image loadable on kernels configured with large pages.
        Assert.All(image.Segments.Where(static s => s.Type == SegmentType.Load && s.MemorySize > 0), static s =>
        {
            Assert.Equal(0x10000UL, s.Alignment);
            Assert.Equal(s.Offset % s.Alignment, s.Address % s.Alignment);
        });
        Assert.Equal(42, RunArm64(output).ExitCode);
    }

    [Fact]
    public void BranchEncodesTheDistanceToALocalFunction()
    {
        var builder = new ElfObjectBuilder(ElfMachine.AArch64);
        var text = builder.Text(".text", Code.Arm64(StpFrame, 0x94000000, LdpFrame, Ret)); // bl helper
        builder.Global("main", text);
        builder.Relocate(text, 4, RelocArm64.Call26, "helper");
        var output = LinkArm64(builder.Build(), Function("helper", 0x528000a0, Ret)); // mov w0, #5 ; ret

        var image = Load(output);
        Assert.Equal(image.Symbols["helper"], BranchTarget(image, image.Symbols["main"] + 4));
        Assert.Equal(5, RunArm64(output).ExitCode);
    }

    [Fact]
    public void PageAndOffsetPairAddressesLocalData()
    {
        var output = LinkArm64(PrintMessageProgram("main"));

        var image = Load(output);
        var main = image.Symbols["main"];
        var address = AdrpTarget(image, main + 4) + Imm12(image, main + 8);
        Assert.Equal("hello\0"u8, image.ReadAt(address, 6));
        Assert.Equal((0, "hello\n"), RunArm64(output));
    }

    [Fact]
    public void CallToImportedFunctionGoesThroughAStubThatLoadsItsGotEntry()
    {
        var image = Load(LinkArm64(PrintMessageProgram("main")));
        var plt = image.GetSection(".plt");
        var got = image.GetSection(".got");

        var stub = BranchTarget(image, image.Symbols["main"] + 12);
        Assert.InRange(stub, plt.Address, plt.Address + plt.Size - 16);

        // adrp x16, page ; ldr x17, [x16, offset] ; add x16, x16, offset ; br x17
        var entry = AdrpTarget(image, stub) + Imm12(image, stub + 4) * 8;
        Assert.InRange(entry, got.Address, got.Address + got.Size - 8);
        Assert.Equal(entry, AdrpTarget(image, stub) + Imm12(image, stub + 8));
        Assert.Equal(0xd61f0220u, LittleEndian.U32(image.ReadAt(stub + 12, 4), 0));
        Assert.Contains("puts", image.DynamicSymbols);
    }

    [Fact]
    public void ImportedDataIsReadThroughTheGlobalOffsetTable()
    {
        var builder = new ElfObjectBuilder(ElfMachine.AArch64);
        var message = builder.ReadOnlyData(".rodata", "via got\n\0"u8);
        var text = builder.Text(".text", Code.Arm64(
            StpFrame,
            0x90000001, // adrp x1, :got:stdout
            0xf9400021, // ldr  x1, [x1, :got_lo12:stdout]
            0xf9400021, // ldr  x1, [x1]
            0x90000000, // adrp x0, message
            0x91000000, // add  x0, x0, :lo12:message
            0x94000000, // bl   fputs
            0x52800000, // mov  w0, #0
            LdpFrame,
            Ret));
        builder.Global("main", text);
        builder.Relocate(text, 4, RelocArm64.AdrGotPage, "stdout");
        builder.Relocate(text, 8, RelocArm64.Ld64GotLo12Nc, "stdout");
        builder.Relocate(text, 16, RelocArm64.AdrPrelPgHi21, message);
        builder.Relocate(text, 20, RelocArm64.AddAbsLo12Nc, message);
        builder.Relocate(text, 24, RelocArm64.Call26, "fputs");

        Assert.Equal((0, "via got\n"), RunArm64(LinkArm64(builder.Build())));
    }

    [Fact]
    public void ThreadLocalDescriptorSequenceIsRewrittenToAConstantOffset()
    {
        var builder = new ElfObjectBuilder(ElfMachine.AArch64);
        var text = builder.Text(".text", Code.Arm64(
            StpFrame,
            0x90000000, // adrp x0, :tlsdesc:variable
            0xf9400001, // ldr  x1, [x0, :tlsdesc_lo12:variable]
            0x91000000, // add  x0, x0, :tlsdesc_lo12:variable
            0xd63f0020, // blr  x1
            0xd53bd048, // mrs  x8, tpidr_el0
            0xb8606900, // ldr  w0, [x8, x0]
            LdpFrame,
            Ret));
        builder.Global("main", text);
        builder.Global("variable", ThreadLocalData(builder), value: 4, type: SymbolType.Tls);
        builder.Relocate(text, 4, RelocArm64.TlsDescAdrPage21, "variable");
        builder.Relocate(text, 8, RelocArm64.TlsDescLd64Lo12, "variable");
        builder.Relocate(text, 12, RelocArm64.TlsDescAddLo12, "variable");
        builder.Relocate(text, 16, RelocArm64.TlsDescCall, "variable");
        var output = LinkArm64(builder.Build());

        // The thread pointer is followed by a 16 byte control block and then the TLS block, where the variable is
        // at offset 4: movz x0, #0, lsl #16 ; movk x0, #20 ; nop ; nop
        var image = Load(output);
        var code = image.ReadAt(image.Symbols["main"] + 4, 16);
        Assert.Equal(0xd2a00000u, LittleEndian.U32(code, 0));
        Assert.Equal(0xf2800000u | (20 << 5), LittleEndian.U32(code, 4));
        Assert.Equal(Nop, LittleEndian.U32(code, 8));
        Assert.Equal(Nop, LittleEndian.U32(code, 12));
        Assert.Equal(33, RunArm64(output).ExitCode);
    }

    [Fact]
    public void ThreadLocalLocalExecAccess()
    {
        var builder = new ElfObjectBuilder(ElfMachine.AArch64);
        var text = builder.Text(".text", Code.Arm64(
            0xd53bd048, // mrs x8, tpidr_el0
            0x91400108, // add x8, x8, :tprel_hi12:variable
            0x91000108, // add x8, x8, :tprel_lo12_nc:variable
            0xb9400100, // ldr w0, [x8]
            Ret));
        builder.Global("main", text);
        builder.Global("variable", ThreadLocalData(builder), value: 4, type: SymbolType.Tls);
        builder.Relocate(text, 4, RelocArm64.TlsLeAddTprelHi12, "variable");
        builder.Relocate(text, 8, RelocArm64.TlsLeAddTprelLo12Nc, "variable");

        Assert.Equal(33, RunArm64(LinkArm64(builder.Build())).ExitCode);
    }

    [Fact]
    public void ThreadLocalInitialExecAccess()
    {
        var builder = new ElfObjectBuilder(ElfMachine.AArch64);
        var text = builder.Text(".text", Code.Arm64(
            0x90000000, // adrp x0, :gottprel:variable
            0xf9400000, // ldr  x0, [x0, :gottprel_lo12:variable]
            0xd53bd048, // mrs  x8, tpidr_el0
            0xb8606900, // ldr  w0, [x8, x0]
            Ret));
        builder.Global("main", text);
        builder.Global("variable", ThreadLocalData(builder), value: 4, type: SymbolType.Tls);
        builder.Relocate(text, 0, RelocArm64.TlsIeAdrGotTprelPage21, "variable");
        builder.Relocate(text, 4, RelocArm64.TlsIeLd64GotTprelLo12Nc, "variable");

        Assert.Equal(33, RunArm64(LinkArm64(builder.Build())).ExitCode);
    }

    [Fact]
    public void RunsConstructorsBeforeMain()
    {
        var builder = new ElfObjectBuilder(ElfMachine.AArch64);
        var counter = builder.NoBits(".bss", 4);
        var text = builder.Text(".text", Code.Arm64(
            0x90000008, // ctor: adrp x8, counter
            0x528000e9, //       mov  w9, #7
            0xb9000109, //       str  w9, [x8, :lo12:counter]
            Ret,
            0x90000008, // main: adrp x8, counter
            0xb9400100, //       ldr  w0, [x8, :lo12:counter]
            Ret));
        var initArray = builder.AddSection(".init_array", new byte[8], SectionFlags.Alloc | SectionFlags.Write, SectionType.InitArray, 8);
        builder.Global("main", text, value: 16);
        builder.Relocate(text, 0, RelocArm64.AdrPrelPgHi21, counter);
        builder.Relocate(text, 8, RelocArm64.Ldst32AbsLo12Nc, counter);
        builder.Relocate(text, 16, RelocArm64.AdrPrelPgHi21, counter);
        builder.Relocate(text, 20, RelocArm64.Ldst32AbsLo12Nc, counter);
        builder.Relocate(initArray, 0, RelocArm64.Abs64, text);

        Assert.Equal(7, RunArm64(LinkArm64(builder.Build())).ExitCode);
    }

    [Fact]
    public void AtexitHandlersRunWhenTheProgramExits()
    {
        var builder = new ElfObjectBuilder(ElfMachine.AArch64);
        var message = builder.ReadOnlyData(".rodata", "bye\0"u8);
        var text = builder.Text(".text", Code.Arm64(
            StpFrame,   // main:
            0x90000000, //          adrp x0, handler
            0x91000000, //          add  x0, x0, :lo12:handler
            0x94000000, //          bl   atexit
            0x52800000, //          mov  w0, #0
            LdpFrame,
            Ret,
            0x90000000, // handler: adrp x0, message
            0x91000000, //          add  x0, x0, :lo12:message
            0x14000000)); //        b    puts
        builder.Global("main", text);
        builder.Local("handler", text, value: 28);
        builder.Relocate(text, 4, RelocArm64.AdrPrelPgHi21, "handler");
        builder.Relocate(text, 8, RelocArm64.AddAbsLo12Nc, "handler");
        builder.Relocate(text, 12, RelocArm64.Call26, "atexit");
        builder.Relocate(text, 28, RelocArm64.AdrPrelPgHi21, message);
        builder.Relocate(text, 32, RelocArm64.AddAbsLo12Nc, message);
        builder.Relocate(text, 36, RelocArm64.Jump26, "puts");

        Assert.Equal((0, "bye\n"), RunArm64(LinkArm64(builder.Build())));
    }

    [Fact]
    public void AbsoluteAddressesAreRelocatedWhenTheImageMoves()
    {
        var builder = new ElfObjectBuilder(ElfMachine.AArch64);
        var message = builder.ReadOnlyData(".rodata", "pointer\0"u8);
        var table = builder.Data(".data", new byte[8]);
        var text = builder.Text(".text", Code.Arm64(
            StpFrame,
            0x90000008, // adrp x8, table
            0xf9400100, // ldr  x0, [x8, :lo12:table]
            0x94000000, // bl   puts
            0x52800000, // mov  w0, #0
            LdpFrame,
            Ret));
        builder.Global("main", text);
        builder.Relocate(text, 4, RelocArm64.AdrPrelPgHi21, table);
        builder.Relocate(text, 8, RelocArm64.Ldst64AbsLo12Nc, table);
        builder.Relocate(text, 12, RelocArm64.Call26, "puts");
        builder.Relocate(table, 0, RelocArm64.Abs64, message);

        Assert.Equal((0, "pointer\n"), RunArm64(LinkArm64(builder.Build())));
    }

    [Fact]
    public void LoadFromAMisalignedAddressIsReported()
    {
        // A 64-bit load encodes its offset in units of 8 bytes, so the low bits of the address must be zero.
        var builder = new ElfObjectBuilder(ElfMachine.AArch64);
        var data = builder.Data(".data", new byte[16]);
        var text = builder.Text(".text", Code.Arm64(0x90000008, 0xf9400100, Ret));
        builder.Global("main", text);
        builder.Relocate(text, 0, RelocArm64.AdrPrelPgHi21, data, 4);
        builder.Relocate(text, 4, RelocArm64.Ldst64AbsLo12Nc, data, 4);

        var error = Assert.Throws<LinkerException>(() => LinkArm64(builder.Build()));

        Assert.Contains("R_AARCH64_LDST64_ABS_LO12_NC", error.Message);
        Assert.Contains("aligned", error.Message);
    }

    [Fact]
    public void UnsupportedRelocationIsReportedByName()
    {
        var builder = new ElfObjectBuilder(ElfMachine.AArch64);
        var text = builder.Text(".text", Code.Arm64(0xd2800000, Ret));
        builder.Global("main", text);
        builder.Relocate(text, 0, RelocArm64.MovwUabsG0, "main");

        var error = Assert.Throws<LinkerException>(() => LinkArm64(builder.Build()));

        Assert.Contains("unsupported relocation", error.Message);
        Assert.Contains("263", error.Message);
    }

    private static byte[] Function(string name, params uint[] instructions)
    {
        var builder = new ElfObjectBuilder(ElfMachine.AArch64);
        builder.Global(name, builder.Text(".text", Code.Arm64(instructions)));
        return builder.Build();
    }

    /// <summary>A function that prints "hello" with puts and returns 0.</summary>
    private static byte[] PrintMessageProgram(string name)
    {
        var builder = new ElfObjectBuilder(ElfMachine.AArch64);
        var message = builder.ReadOnlyData(".rodata", "hello\0"u8);
        var text = builder.Text(".text", Code.Arm64(
            StpFrame,
            0x90000000, // adrp x0, message
            0x91000000, // add  x0, x0, :lo12:message
            0x94000000, // bl   puts
            0x52800000, // mov  w0, #0
            LdpFrame,
            Ret));
        builder.Global(name, text);
        builder.Relocate(text, 4, RelocArm64.AdrPrelPgHi21, message);
        builder.Relocate(text, 8, RelocArm64.AddAbsLo12Nc, message);
        builder.Relocate(text, 12, RelocArm64.Call26, "puts");
        return builder.Build();
    }

    /// <summary>A .tdata section holding the 32-bit values 11 and 33.</summary>
    private static ElfObjectBuilder.Section ThreadLocalData(ElfObjectBuilder builder) =>
        builder.AddSection(".tdata", [0x0b, 0, 0, 0, 0x21, 0, 0, 0], SectionFlags.Alloc | SectionFlags.Write | SectionFlags.Tls, alignment: 4);

    private static uint Instruction(ElfImage image, ulong address) => LittleEndian.U32(image.ReadAt(address, 4), 0);

    /// <summary>Decodes the destination of a B or BL instruction.</summary>
    private static ulong BranchTarget(ElfImage image, ulong address)
    {
        var displacement = (int)(Instruction(image, address) << 6) >> 6;
        return address + (ulong)((long)displacement * 4);
    }

    /// <summary>Decodes the page address an ADRP instruction produces.</summary>
    private static ulong AdrpTarget(ElfImage image, ulong address)
    {
        var instruction = Instruction(image, address);
        var immediate = (int)((((instruction >> 5) & 0x7ffff) << 2) | ((instruction >> 29) & 3));
        immediate = (immediate << 11) >> 11;
        return (address & ~0xfffUL) + (ulong)((long)immediate << 12);
    }

    /// <summary>Decodes the unscaled 12-bit immediate of an ADD or load/store instruction.</summary>
    private static ulong Imm12(ElfImage image, ulong address) => (Instruction(image, address) >> 10) & 0xfff;
}
