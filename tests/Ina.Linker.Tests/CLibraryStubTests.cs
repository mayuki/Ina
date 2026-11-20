using Ina.Linker.Elf;
using Ina.Linker.Tests.Support;

namespace Ina.Linker.Tests;

/// <summary>
/// Some C library entry points are not exported by the shared library; a native toolchain links them statically
/// from <c>libc_nonshared.a</c>. The linker has to supply equivalents itself.
/// </summary>
public class CLibraryStubTests : ElfLinkerTestBase
{
    [Fact]
    public void AtexitHandlersRunWhenTheProgramExits()
    {
        var builder = new ElfObjectBuilder();
        var message = builder.ReadOnlyData(".rodata", "bye\0"u8);
        var text = builder.Text(".text",
            0x48, 0x83, 0xec, 0x08,                   // main:    sub  rsp, 8
            0x48, 0x8d, 0x3d, 0x00, 0x00, 0x00, 0x00, //          lea  rdi, [rip + handler]
            0xe8, 0x00, 0x00, 0x00, 0x00,             //          call atexit
            0x31, 0xc0,                               //          xor  eax, eax
            0x48, 0x83, 0xc4, 0x08,                   //          add  rsp, 8
            0xc3,                                     //          ret
            0x48, 0x8d, 0x3d, 0x00, 0x00, 0x00, 0x00, // handler: lea  rdi, [rip + message]
            0xe9, 0x00, 0x00, 0x00, 0x00);            //          jmp  puts
        builder.Global("main", text);
        builder.Local("handler", text, value: 23);
        builder.Relocate(text, 7, RelocX64.Pc32, "handler", -4);
        builder.Relocate(text, 12, RelocX64.Plt32, "atexit", -4);
        builder.Relocate(text, 26, RelocX64.Pc32, message, -4);
        builder.Relocate(text, 31, RelocX64.Plt32, "puts", -4);

        Assert.Equal((0, "bye\n"), Run(Link(builder.Build())));
    }

    [Fact]
    public void StubsAreOnlyAddedWhenReferenced()
    {
        var image = Load(Link(Code.Function("main", Code.Return(0))));

        Assert.DoesNotContain("atexit", image.Symbols.Keys);
        Assert.DoesNotContain("__cxa_atexit", image.DynamicSymbols);
    }

    [Fact]
    public void DefinitionFromAnInputTakesPrecedenceOverTheStub()
    {
        var output = Link(Code.MainCalling("atexit"), Code.Function("atexit", Code.Return(9)));

        Assert.Equal(9, Run(output).ExitCode);
    }
}
