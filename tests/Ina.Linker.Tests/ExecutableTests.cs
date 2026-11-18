using Ina.Linker.Elf;
using Ina.Linker.Tests.Support;

namespace Ina.Linker.Tests;

/// <summary>Links small programs and runs them: the linked image has to work, not just look right.</summary>
public class ExecutableTests : LinkerTestBase
{
    [Fact]
    public void ReturnValueOfMainBecomesTheExitCode()
    {
        var output = Link(Code.Function("main", Code.Return(42)));

        Assert.Equal(42, Run(output).ExitCode);
    }

    [Fact]
    public void CallsImportedFunctionThroughStub()
    {
        var builder = new ElfObjectBuilder();
        var message = builder.ReadOnlyData(".rodata", "hello from ina\0"u8);
        var text = builder.Text(".text",
            0x48, 0x83, 0xec, 0x08,                   // sub  rsp, 8
            0x48, 0x8d, 0x3d, 0x00, 0x00, 0x00, 0x00, // lea  rdi, [rip + message]
            0xe8, 0x00, 0x00, 0x00, 0x00,             // call puts
            0x31, 0xc0,                               // xor  eax, eax
            0x48, 0x83, 0xc4, 0x08,                   // add  rsp, 8
            0xc3);                                    // ret
        builder.Global("main", text);
        builder.Relocate(text, 7, RelocX64.Pc32, message, -4);
        builder.Relocate(text, 12, RelocX64.Plt32, "puts", -4);

        var (exitCode, output) = Run(Link(builder.Build()));

        Assert.Equal(0, exitCode);
        Assert.Equal("hello from ina\n", output);
    }

    [Fact]
    public void ReadsImportedDataThroughGlobalOffsetTable()
    {
        var builder = new ElfObjectBuilder();
        var message = builder.ReadOnlyData(".rodata", "via got\n\0"u8);
        var text = builder.Text(".text",
            0x48, 0x83, 0xec, 0x08,                   // sub  rsp, 8
            0x48, 0x8b, 0x35, 0x00, 0x00, 0x00, 0x00, // mov  rsi, [rip + stdout@GOT]
            0x48, 0x8b, 0x36,                         // mov  rsi, [rsi]
            0x48, 0x8d, 0x3d, 0x00, 0x00, 0x00, 0x00, // lea  rdi, [rip + message]
            0xe8, 0x00, 0x00, 0x00, 0x00,             // call fputs
            0x31, 0xc0,                               // xor  eax, eax
            0x48, 0x83, 0xc4, 0x08,                   // add  rsp, 8
            0xc3);                                    // ret
        builder.Global("main", text);
        builder.Relocate(text, 7, RelocX64.RexGotPcRelX, "stdout", -4);
        builder.Relocate(text, 17, RelocX64.Pc32, message, -4);
        builder.Relocate(text, 22, RelocX64.Plt32, "fputs", -4);

        var (exitCode, output) = Run(Link(builder.Build()));

        Assert.Equal(0, exitCode);
        Assert.Equal("via got\n", output);
    }

    [Fact]
    public void RunsConstructorsBeforeMain()
    {
        var builder = new ElfObjectBuilder();
        var counter = builder.NoBits(".bss", 4);
        var text = builder.Text(".text",
            0xc7, 0x05, 0x00, 0x00, 0x00, 0x00, 0x07, 0x00, 0x00, 0x00, // ctor: mov dword [rip + counter], 7
            0xc3,                                                       //       ret
            0x8b, 0x05, 0x00, 0x00, 0x00, 0x00,                         // main: mov eax, [rip + counter]
            0xc3);                                                      //       ret
        var initArray = builder.AddSection(".init_array", new byte[8], SectionFlags.Alloc | SectionFlags.Write, SectionType.InitArray, 8);
        builder.Global("main", text, value: 11);
        builder.Relocate(text, 2, RelocX64.Pc32, counter, -8);
        builder.Relocate(text, 13, RelocX64.Pc32, counter, -4);
        builder.Relocate(initArray, 0, RelocX64.Abs64, text);

        Assert.Equal(7, Run(Link(builder.Build())).ExitCode);
    }

    [Fact]
    public void AbsoluteAddressesAreRelocatedWhenTheImageMoves()
    {
        // The pointer stored in .data needs a relative relocation because a PIE is loaded at a random address.
        var output = Link(PointerTableProgram());

        var image = Load(output);
        Assert.Equal(ElfType.Dyn, image.Type);
        Assert.Equal((0, "pointer\n"), Run(output));
    }

    [Fact]
    public void FixedAddressExecutableNeedsNoRelativeRelocations()
    {
        var output = Link(static o => o with { PositionIndependent = false }, PointerTableProgram());

        var image = Load(output);
        Assert.Equal(ElfType.Exec, image.Type);
        Assert.DoesNotContain(image.DynamicEntries, static e => e.Tag == DynamicTag.RelaCount);
        Assert.Equal((0, "pointer\n"), Run(output));
    }

    [Fact]
    public void FixedAddressExecutableAllows32BitAbsoluteAddresses()
    {
        var builder = new ElfObjectBuilder();
        var message = builder.ReadOnlyData(".rodata", "absolute\0"u8);
        var text = builder.Text(".text",
            0x48, 0x83, 0xec, 0x08,       // sub  rsp, 8
            0xbf, 0x00, 0x00, 0x00, 0x00, // mov  edi, message
            0xe8, 0x00, 0x00, 0x00, 0x00, // call puts
            0x31, 0xc0,                   // xor  eax, eax
            0x48, 0x83, 0xc4, 0x08,       // add  rsp, 8
            0xc3);                        // ret
        builder.Global("main", text);
        builder.Relocate(text, 5, RelocX64.Abs32, message);
        builder.Relocate(text, 10, RelocX64.Plt32, "puts", -4);
        var input = builder.Build();

        Assert.Equal((0, "absolute\n"), Run(Link(static o => o with { PositionIndependent = false }, input)));

        // The same code cannot work in an image that is loaded at an unknown address.
        var error = Assert.Throws<LinkerException>(() => Link(input));
        Assert.Contains("R_X86_64_32", error.Message);
    }

    [Fact]
    public void ThreadLocalGeneralDynamicAccess()
    {
        var builder = new ElfObjectBuilder();
        var text = builder.Text(".text",
            0x48, 0x83, 0xec, 0x08,                         // sub  rsp, 8
            0x66, 0x48, 0x8d, 0x3d, 0x00, 0x00, 0x00, 0x00, // lea  rdi, [rip + variable@TLSGD]
            0x66, 0x66, 0x48, 0xe8, 0x00, 0x00, 0x00, 0x00, // call __tls_get_addr
            0x8b, 0x00,                                     // mov  eax, [rax]
            0x48, 0x83, 0xc4, 0x08,                         // add  rsp, 8
            0xc3);                                          // ret
        builder.Global("main", text);
        builder.Global("variable", ThreadLocalData(builder, 33), value: 4, type: SymbolType.Tls);
        builder.Relocate(text, 8, RelocX64.TlsGd, "variable", -4);
        builder.Relocate(text, 16, RelocX64.Plt32, "__tls_get_addr", -4);

        Assert.Equal(33, Run(Link(builder.Build())).ExitCode);
    }

    [Fact]
    public void ThreadLocalLocalDynamicAccess()
    {
        var builder = new ElfObjectBuilder();
        var text = builder.Text(".text",
            0x48, 0x83, 0xec, 0x08,                   // sub  rsp, 8
            0x48, 0x8d, 0x3d, 0x00, 0x00, 0x00, 0x00, // lea  rdi, [rip + variable@TLSLD]
            0xe8, 0x00, 0x00, 0x00, 0x00,             // call __tls_get_addr
            0x8b, 0x80, 0x00, 0x00, 0x00, 0x00,       // mov  eax, [rax + variable@DTPOFF]
            0x48, 0x83, 0xc4, 0x08,                   // add  rsp, 8
            0xc3);                                    // ret
        builder.Global("main", text);
        builder.Global("variable", ThreadLocalData(builder, 34), value: 4, type: SymbolType.Tls);
        builder.Relocate(text, 7, RelocX64.TlsLd, "variable", -4);
        builder.Relocate(text, 12, RelocX64.Plt32, "__tls_get_addr", -4);
        builder.Relocate(text, 18, RelocX64.DtpOff32, "variable");

        Assert.Equal(34, Run(Link(builder.Build())).ExitCode);
    }

    [Fact]
    public void ThreadLocalInitialExecAccess()
    {
        var builder = new ElfObjectBuilder();
        var text = builder.Text(".text",
            0x48, 0x8b, 0x05, 0x00, 0x00, 0x00, 0x00, // mov rax, [rip + variable@GOTTPOFF]
            0x64, 0x8b, 0x00,                         // mov eax, fs:[rax]
            0xc3);                                    // ret
        builder.Global("main", text);
        builder.Global("variable", ThreadLocalData(builder, 35), value: 4, type: SymbolType.Tls);
        builder.Relocate(text, 3, RelocX64.GotTpOff, "variable", -4);

        Assert.Equal(35, Run(Link(builder.Build())).ExitCode);
    }

    [Fact]
    public void ThreadLocalLocalExecAccessToInitializedAndZeroedVariables()
    {
        var builder = new ElfObjectBuilder();
        var text = builder.Text(".text",
            0x64, 0x8b, 0x04, 0x25, 0x00, 0x00, 0x00, 0x00, // mov eax, fs:[zeroed@TPOFF]
            0x83, 0xc0, 0x05,                               // add eax, 5
            0x64, 0x89, 0x04, 0x25, 0x00, 0x00, 0x00, 0x00, // mov fs:[zeroed@TPOFF], eax
            0x64, 0x8b, 0x04, 0x25, 0x00, 0x00, 0x00, 0x00, // mov eax, fs:[zeroed@TPOFF]
            0x64, 0x03, 0x04, 0x25, 0x00, 0x00, 0x00, 0x00, // add eax, fs:[variable@TPOFF]
            0xc3);                                          // ret
        builder.Global("main", text);
        builder.Global("variable", ThreadLocalData(builder, 36), value: 4, type: SymbolType.Tls);

        // A more strictly aligned zero-initialized block exercises the padding between .tdata and .tbss.
        var zeroed = builder.NoBits(".tbss", 4, SectionFlags.Alloc | SectionFlags.Write | SectionFlags.Tls, alignment: 64);
        builder.Global("zeroed", zeroed, type: SymbolType.Tls);
        builder.Relocate(text, 4, RelocX64.TpOff32, "zeroed");
        builder.Relocate(text, 15, RelocX64.TpOff32, "zeroed");
        builder.Relocate(text, 23, RelocX64.TpOff32, "zeroed");
        builder.Relocate(text, 31, RelocX64.TpOff32, "variable");

        Assert.Equal(41, Run(Link(builder.Build())).ExitCode);
    }

    /// <summary>A .tdata section holding the 32-bit values 11 and <paramref name="value"/>.</summary>
    private static ElfObjectBuilder.Section ThreadLocalData(ElfObjectBuilder builder, int value) =>
        builder.AddSection(".tdata", [0x0b, 0, 0, 0, (byte)value, 0, 0, 0], SectionFlags.Alloc | SectionFlags.Write | SectionFlags.Tls, alignment: 4);

    /// <summary>A program that prints a string through a pointer stored in <c>.data</c>.</summary>
    private static byte[] PointerTableProgram()
    {
        var builder = new ElfObjectBuilder();
        var message = builder.ReadOnlyData(".rodata", "pointer\0"u8);
        var table = builder.Data(".data", new byte[8]);
        var text = builder.Text(".text",
            0x48, 0x83, 0xec, 0x08,                   // sub  rsp, 8
            0x48, 0x8b, 0x3d, 0x00, 0x00, 0x00, 0x00, // mov  rdi, [rip + table]
            0xe8, 0x00, 0x00, 0x00, 0x00,             // call puts
            0x31, 0xc0,                               // xor  eax, eax
            0x48, 0x83, 0xc4, 0x08,                   // add  rsp, 8
            0xc3);                                    // ret
        builder.Global("main", text);
        builder.Relocate(text, 7, RelocX64.Pc32, table, -4);
        builder.Relocate(text, 12, RelocX64.Plt32, "puts", -4);
        builder.Relocate(table, 0, RelocX64.Abs64, message);
        return builder.Build();
    }
}
