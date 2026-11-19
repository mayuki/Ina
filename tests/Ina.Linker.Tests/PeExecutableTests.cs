using Ina.Linker.Pe;
using Ina.Linker.Tests.Support;

namespace Ina.Linker.Tests;

/// <summary>
/// Links small programs into Windows executables and runs them: the linked image has to work, not just look right.
/// All programs start in the startup code that the linker generates, which calls their <c>wmain</c>.
/// </summary>
public class PeExecutableTests : PeLinkerTestBase
{
    [Fact]
    public void ReturnValueOfMainBecomesTheExitCode()
    {
        var output = Link(WindowsCode.MainReturning(42));

        Assert.Equal(42, Run(output).ExitCode);
    }

    [Fact]
    public void CallsImportedFunctionThroughThunk()
    {
        var builder = new CoffObjectBuilder();
        var message = builder.ReadOnlyData(".rdata", "hello from ina\0"u8);
        var text = builder.Text(".text$mn",
            0x48, 0x83, 0xec, 0x28,                   // sub  rsp, 0x28
            0x48, 0x8d, 0x0d, 0x00, 0x00, 0x00, 0x00, // lea  rcx, [rip + message]
            0xe8, 0x00, 0x00, 0x00, 0x00,             // call puts
            0x33, 0xc0,                               // xor  eax, eax
            0x48, 0x83, 0xc4, 0x28,                   // add  rsp, 0x28
            0xc3);                                    // ret
        builder.External("wmain", text);
        builder.Relocate(text, 7, RelAmd64.Rel32, message);
        builder.Relocate(text, 12, RelAmd64.Rel32, "puts");

        var output = Link(builder.Build());

        // The function comes from the C runtime DLL, addressed through its API set like in official binaries.
        Assert.Contains(Load(output).Imports, static i => i.Library == "api-ms-win-crt-stdio-l1-1-0.dll" && i.Names.Contains("puts"));
        Assert.Equal((0, "hello from ina\r\n"), Run(output));
    }

    [Fact]
    public void CallsImportedFunctionThroughItsAddressSlot()
    {
        // This is what compilers emit for functions that are declared as imported: an indirect call through __imp_X.
        var builder = new CoffObjectBuilder();
        var message = builder.ReadOnlyData(".rdata", "through the table\0"u8);
        var text = builder.Text(".text$mn",
            0x48, 0x83, 0xec, 0x28,                   // sub  rsp, 0x28
            0x48, 0x8d, 0x0d, 0x00, 0x00, 0x00, 0x00, // lea  rcx, [rip + message]
            0xff, 0x15, 0x00, 0x00, 0x00, 0x00,       // call qword ptr [rip + __imp_puts]
            0x33, 0xc0,                               // xor  eax, eax
            0x48, 0x83, 0xc4, 0x28,                   // add  rsp, 0x28
            0xc3);                                    // ret
        builder.External("wmain", text);
        builder.Relocate(text, 7, RelAmd64.Rel32, message);
        builder.Relocate(text, 13, RelAmd64.Rel32, "__imp_puts");

        Assert.Equal((0, "through the table\r\n"), Run(Link(builder.Build())));
    }

    [Fact]
    public void RunsCppInitializersBeforeMain()
    {
        var builder = new CoffObjectBuilder();
        var counter = builder.Bss(".bss", 4);
        var text = builder.Text(".text$mn",
            0xc7, 0x05, 0x00, 0x00, 0x00, 0x00, 0x07, 0x00, 0x00, 0x00, // ctor:  mov dword ptr [rip + counter], 7
            0xc3,                                                       //        ret
            0x8b, 0x05, 0x00, 0x00, 0x00, 0x00,                         // wmain: mov eax, [rip + counter]
            0xc3);                                                      //        ret
        builder.External("wmain", text, value: 11);

        // Four bytes of immediate follow the displacement, which the relocation type accounts for.
        builder.Relocate(text, 2, RelAmd64.Rel32_4, counter);
        builder.Relocate(text, 13, RelAmd64.Rel32, counter);

        // Compilers put the addresses of static constructors into this section.
        var initializers = builder.ReadOnlyData(".CRT$XCU", new byte[8], 8);
        builder.Relocate(initializers, 0, RelAmd64.Addr64, text);

        Assert.Equal(7, Run(Link(builder.Build())).ExitCode);
    }

    [Fact]
    public void AbsoluteAddressesAreRelocatedWhenTheImageMoves()
    {
        var output = Link(PointerTableProgram());

        // The pointer stored in .data is listed for the loader, and the image declares that it may be moved.
        var image = Load(output);
        var pointer = Assert.Single(image.BaseRelocations, rva => image.SectionOf(rva).Name == ".data");
        Assert.Equal("pointer", image.CString((uint)(image.U64(pointer) - image.ImageBase)));
        Assert.Equal(0x0160, image.DllCharacteristics & 0x0160); // high entropy, dynamic base, NX compatible
        Assert.Equal(".reloc", image.Sections[^1].Name);
        Assert.Equal((0, "pointer\r\n"), Run(output));
    }

    [Fact]
    public void FixedAddressExecutableHasNoBaseRelocations()
    {
        var output = Link(static o => o with { DynamicBase = false }, PointerTableProgram());

        var image = Load(output);
        Assert.Empty(image.BaseRelocations);
        Assert.Null(image.FindSection(".reloc"));
        Assert.Equal(0x0001, image.Characteristics & 0x0001); // relocations stripped
        Assert.Equal(0, image.DllCharacteristics & 0x0040);   // not movable
        Assert.Equal((0, "pointer\r\n"), Run(output));
    }

    [Fact]
    public void ThreadLocalVariablesStartWithTheirInitialValues()
    {
        var builder = new CoffObjectBuilder();
        const uint Template = CoffSectionFlags.InitializedData | CoffSectionFlags.Read | CoffSectionFlags.Write;
        var first = builder.AddSection(".tls$", [7, 0, 0, 0], Template, 4);
        var second = builder.AddSection(".tls$B", [35, 0, 0, 0], Template, 16);
        var text = builder.Text(".text$mn",
            0x8b, 0x05, 0x00, 0x00, 0x00, 0x00,                   // mov eax, [rip + _tls_index]
            0x65, 0x48, 0x8b, 0x0c, 0x25, 0x58, 0x00, 0x00, 0x00, // mov rcx, gs:[0x58]          ; TLS array of the thread
            0x48, 0x8b, 0x0c, 0xc1,                               // mov rcx, [rcx + rax * 8]    ; TLS block of the program
            0x8b, 0x81, 0x00, 0x00, 0x00, 0x00,                   // mov eax, [rcx + first]
            0x03, 0x81, 0x00, 0x00, 0x00, 0x00,                   // add eax, [rcx + second]
            0xc3);                                                // ret
        builder.External("wmain", text);
        builder.Relocate(text, 2, RelAmd64.Rel32, "_tls_index");
        builder.Relocate(text, 21, RelAmd64.SecRel, first);
        builder.Relocate(text, 27, RelAmd64.SecRel, second);

        var output = Link(builder.Build());

        // The TLS directory describes an initialization image that holds both variables, and tells the loader to
        // align the copies of each thread like the strictest variable.
        var image = Load(output);
        var (directory, size) = image.Directory(PeImage.DirectoryTls);
        Assert.Equal(40u, size);
        var start = (uint)(image.U64(directory) - image.ImageBase);
        var end = (uint)(image.U64(directory + 8) - image.ImageBase);
        Assert.Equal(".tls", image.SectionOf(start).Name);
        Assert.True(end - start >= 20, $"initialization image of {end - start} bytes");
        Assert.Equal(5u << 20, image.U32(directory + 36)); // IMAGE_SCN_ALIGN_16BYTES
        Assert.Equal(42, Run(output).ExitCode);
    }

    [Fact]
    public void DynamicInitializersOfThreadLocalsRunForTheMainThread()
    {
        var builder = new CoffObjectBuilder();
        var state = builder.Bss(".bss", 4);
        var text = builder.Text(".text$mn",
            0xc7, 0x05, 0x00, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00, // init:  mov dword ptr [rip + state], 9
            0xc3,                                                       //        ret
            0x8b, 0x05, 0x00, 0x00, 0x00, 0x00,                         // wmain: mov eax, [rip + state]
            0xc3);                                                      //        ret
        builder.External("wmain", text, value: 11);
        builder.Relocate(text, 2, RelAmd64.Rel32_4, state);
        builder.Relocate(text, 13, RelAmd64.Rel32, state);

        // Where compilers register the initializers of thread_local variables that are not constants.
        var initializers = builder.ReadOnlyData(".CRT$XDU", new byte[8], 8);
        builder.Relocate(initializers, 0, RelAmd64.Addr64, text);

        Assert.Equal(9, Run(Link(builder.Build())).ExitCode);
    }

    [Fact]
    public void ExitHandlersRunWhenMainReturns()
    {
        // atexit is a function of the static C runtime; Ina maps it to the registration function of the DLL.
        var builder = new CoffObjectBuilder();
        var message = builder.ReadOnlyData(".rdata", "bye\0"u8);
        var text = builder.Text(".text$mn",
            0x48, 0x83, 0xec, 0x28,                   // wmain:   sub  rsp, 0x28
            0x48, 0x8d, 0x0d, 0x15, 0x00, 0x00, 0x00, //          lea  rcx, [rip + handler]
            0xe8, 0x00, 0x00, 0x00, 0x00,             //          call atexit
            0x33, 0xc0,                               //          xor  eax, eax
            0x48, 0x83, 0xc4, 0x28,                   //          add  rsp, 0x28
            0xc3,                                     //          ret
            0xcc, 0xcc, 0xcc, 0xcc, 0xcc, 0xcc, 0xcc, 0xcc, 0xcc,
            0x48, 0x83, 0xec, 0x28,                   // handler: sub  rsp, 0x28
            0x48, 0x8d, 0x0d, 0x00, 0x00, 0x00, 0x00, //          lea  rcx, [rip + message]
            0xe8, 0x00, 0x00, 0x00, 0x00,             //          call puts
            0x48, 0x83, 0xc4, 0x28,                   //          add  rsp, 0x28
            0xc3);                                    //          ret
        builder.External("wmain", text);
        builder.Relocate(text, 12, RelAmd64.Rel32, "atexit");
        builder.Relocate(text, 39, RelAmd64.Rel32, message);
        builder.Relocate(text, 44, RelAmd64.Rel32, "puts");

        Assert.Equal((0, "bye\r\n"), Run(Link(builder.Build())));
    }

    [Fact]
    public void LoaderRandomizesTheStackProtectionCookie()
    {
        // The cookie starts with a well-known value; Windows replaces it when it loads the image because the load
        // configuration directory that the linker contributes points at it.
        var builder = new CoffObjectBuilder();
        var text = builder.Text(".text$mn",
            0x48, 0x8b, 0x05, 0x00, 0x00, 0x00, 0x00,                   // mov   rax, [rip + __security_cookie]
            0x48, 0xb9, 0x32, 0xa2, 0xdf, 0x2d, 0x99, 0x2b, 0x00, 0x00, // mov   rcx, 0x00002b992ddfa232
            0x48, 0x3b, 0xc1,                                           // cmp   rax, rcx
            0x0f, 0x94, 0xc0,                                           // sete  al
            0x0f, 0xb6, 0xc0,                                           // movzx eax, al
            0xc3);                                                      // ret
        builder.External("wmain", text);
        builder.Relocate(text, 3, RelAmd64.Rel32, "__security_cookie");

        var output = Link(builder.Build());

        var image = Load(output);
        var (configuration, size) = image.Directory(PeImage.DirectoryLoadConfig);
        Assert.Equal(image.U32(configuration), size);
        Assert.Equal(0x0000_2b99_2ddf_a232ul, image.U64((uint)(image.U64(configuration + 0x58) - image.ImageBase)));
        Assert.Equal(0, Run(output).ExitCode);
    }

    [Fact]
    public void StackProtectionCheckAcceptsTheCookieAndStopsTheProgramOtherwise()
    {
        static byte[] Program(bool corrupt)
        {
            var builder = new CoffObjectBuilder();
            var text = builder.Text(".text$mn",
                0x48, 0x83, 0xec, 0x28,                   // sub  rsp, 0x28
                0x48, 0x8b, 0x0d, 0x00, 0x00, 0x00, 0x00, // mov  rcx, [rip + __security_cookie]
                0x48, 0x83, 0xc1, (byte)(corrupt ? 1 : 0), // add  rcx, <0 or 1>
                0xe8, 0x00, 0x00, 0x00, 0x00,             // call __security_check_cookie
                0xb8, 0x05, 0x00, 0x00, 0x00,             // mov  eax, 5
                0x48, 0x83, 0xc4, 0x28,                   // add  rsp, 0x28
                0xc3);                                    // ret
            builder.External("wmain", text);
            builder.Relocate(text, 7, RelAmd64.Rel32, "__security_cookie");
            builder.Relocate(text, 16, RelAmd64.Rel32, "__security_check_cookie");
            return builder.Build();
        }

        Assert.Equal(5, Run(Link(Program(corrupt: false))).ExitCode);

        // STATUS_STACK_BUFFER_OVERRUN, raised by the fail-fast interrupt.
        Assert.Equal(unchecked((int)0xc0000409), Run(Link(Program(corrupt: true))).ExitCode);
    }

    [Fact]
    public void StackProbeCommitsTheStackPageByPage()
    {
        // A megabyte of locals: touching its far end directly would skip the guard page and crash, so compilers
        // call __chkstk first, which Ina generates.
        var builder = new CoffObjectBuilder();
        var text = builder.Text(".text$mn",
            0xb8, 0x00, 0x00, 0x10, 0x00, // mov  eax, 0x100000
            0xe8, 0x00, 0x00, 0x00, 0x00, // call __chkstk
            0x48, 0x2b, 0xe0,             // sub  rsp, rax
            0xc6, 0x04, 0x24, 0x01,       // mov  byte ptr [rsp], 1
            0x48, 0x03, 0xe0,             // add  rsp, rax
            0x2d, 0xfb, 0xff, 0x0f, 0x00, // sub  eax, 0xffffb       ; rax must survive the call
            0xc3);                        // ret
        builder.External("wmain", text);
        builder.Relocate(text, 6, RelAmd64.Rel32, "__chkstk");

        Assert.Equal(5, Run(Link(builder.Build())).ExitCode);
    }

    [Fact]
    public void GraphicalApplicationsAreMarkedForTheWindowsSubsystem()
    {
        var output = Link(static o => o with { Subsystem = PeSubsystem.Windows }, WindowsCode.MainReturning(3));

        Assert.Equal(2, Load(output).Subsystem);
        Assert.Equal(3, Run(output).ExitCode);
    }

    [Fact]
    public void ProgramWithItsOwnEntryPointNeedsNoRuntimeSupport()
    {
        var output = Link(static o => o with { EntryPoint = "start" }, WindowsCode.Function("start", WindowsCode.Return(11)));

        var image = Load(output);
        Assert.Empty(image.Imports);
        Assert.Equal((0u, 0u), image.Directory(PeImage.DirectoryTls));
        Assert.Equal(11, Run(output).ExitCode);
    }

    /// <summary>A program that prints a string whose address is stored in a writable pointer.</summary>
    private static byte[] PointerTableProgram()
    {
        var builder = WindowsCode.MainPrinting("table");
        var message = builder.ReadOnlyData(".rdata", "pointer\0"u8);
        var table = builder.Data(".data", new byte[8]);
        builder.External("table", table);
        builder.Relocate(table, 0, RelAmd64.Addr64, message);
        return builder.Build();
    }
}
