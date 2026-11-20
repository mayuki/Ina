using Ina.Linker.Elf;
using Ina.Linker.Tests.Support;

namespace Ina.Linker.Tests;

/// <summary>
/// In an executable the offset of a thread-local variable from the thread pointer is a link-time constant. The
/// general-dynamic and local-dynamic access sequences, which call <c>__tls_get_addr</c>, are therefore rewritten to
/// plain thread pointer arithmetic, as the x86-64 ABI allows when the instructions have the canonical form.
/// </summary>
public class ThreadLocalRelaxationTests : ElfLinkerTestBase
{
    [Fact]
    public void GeneralDynamicSequenceIsRewrittenToThreadPointerArithmetic()
    {
        var output = Link(GeneralDynamic(canonical: true));
        var image = Load(output);

        // mov rax, fs:[0] ; lea rax, [rax + offset]  - the variable is 4 bytes before the end of the 8 byte block.
        byte[] expected = [0x64, 0x48, 0x8b, 0x04, 0x25, 0x00, 0x00, 0x00, 0x00, 0x48, 0x8d, 0x80, 0xfc, 0xff, 0xff, 0xff];
        Assert.Equal(expected, image.ReadAt(image.Symbols["main"] + 4, 16));
        Assert.DoesNotContain("__tls_get_addr", image.DynamicSymbols);
        Assert.Equal(33, Run(output).ExitCode);
    }

    [Fact]
    public void GeneralDynamicSequenceInAnotherFormKeepsCallingTheLoader()
    {
        // Without the padding prefixes there is no room for the replacement instructions.
        var output = Link(GeneralDynamic(canonical: false));

        Assert.Contains("__tls_get_addr", Load(output).DynamicSymbols);
        Assert.Equal(33, Run(output).ExitCode);
    }

    [Fact]
    public void LocalDynamicSequenceIsRewrittenToThreadPointerArithmetic()
    {
        var output = Link(LocalDynamic("main", canonical: true));
        var image = Load(output);

        // Three padding prefixes, then mov rax, fs:[0]; the following displacement becomes relative to the thread pointer.
        byte[] expected = [0x66, 0x66, 0x66, 0x64, 0x48, 0x8b, 0x04, 0x25, 0x00, 0x00, 0x00, 0x00, 0x8b, 0x80, 0xfc, 0xff, 0xff, 0xff];
        Assert.Equal(expected, image.ReadAt(image.Symbols["main"] + 4, 18));
        Assert.DoesNotContain("__tls_get_addr", image.DynamicSymbols);
        Assert.Equal(34, Run(output).ExitCode);
    }

    [Fact]
    public void LocalDynamicIsOnlyRewrittenWhenEveryAccessHasTheCanonicalForm()
    {
        // Module-relative offsets are shared by all local-dynamic accesses, so a single access that cannot be
        // rewritten forces all of them to keep calling the loader.
        // Garbage collection is off so that the second function, which nothing calls, takes part in the link.
        var output = Link(
            static o => o with { GarbageCollectSections = false },
            LocalDynamic("main", canonical: true), LocalDynamic("other", canonical: false, defineVariable: false));
        var image = Load(output);

        Assert.Equal<byte>([0x48, 0x8d, 0x3d], image.ReadAt(image.Symbols["main"] + 4, 3));
        Assert.Contains("__tls_get_addr", image.DynamicSymbols);
        Assert.Equal(34, Run(output).ExitCode);
    }

    private static byte[] GeneralDynamic(bool canonical)
    {
        var builder = new ElfObjectBuilder();
        ElfObjectBuilder.Section text;
        if (canonical)
        {
            text = builder.Text(".text",
                0x48, 0x83, 0xec, 0x08,                         // sub  rsp, 8
                0x66, 0x48, 0x8d, 0x3d, 0x00, 0x00, 0x00, 0x00, // lea  rdi, [rip + variable@TLSGD]   (with padding prefix)
                0x66, 0x66, 0x48, 0xe8, 0x00, 0x00, 0x00, 0x00, // call __tls_get_addr                (with padding prefixes)
                0x8b, 0x00,                                     // mov  eax, [rax]
                0x48, 0x83, 0xc4, 0x08,                         // add  rsp, 8
                0xc3);                                          // ret
            builder.Relocate(text, 8, RelocX64.TlsGd, "variable", -4);
            builder.Relocate(text, 16, RelocX64.Plt32, "__tls_get_addr", -4);
        }
        else
        {
            text = builder.Text(".text",
                0x48, 0x83, 0xec, 0x08,                   // sub  rsp, 8
                0x48, 0x8d, 0x3d, 0x00, 0x00, 0x00, 0x00, // lea  rdi, [rip + variable@TLSGD]
                0xe8, 0x00, 0x00, 0x00, 0x00,             // call __tls_get_addr
                0x8b, 0x00,                               // mov  eax, [rax]
                0x48, 0x83, 0xc4, 0x08,                   // add  rsp, 8
                0xc3);                                    // ret
            builder.Relocate(text, 7, RelocX64.TlsGd, "variable", -4);
            builder.Relocate(text, 12, RelocX64.Plt32, "__tls_get_addr", -4);
        }

        builder.Global("main", text);
        builder.Global("variable", ThreadLocalData(builder, 33), value: 4, type: SymbolType.Tls);
        return builder.Build();
    }

    private static byte[] LocalDynamic(string function, bool canonical, bool defineVariable = true)
    {
        var builder = new ElfObjectBuilder();
        ElfObjectBuilder.Section text;
        if (canonical)
        {
            text = builder.Text(".text",
                0x48, 0x83, 0xec, 0x08,                   // sub  rsp, 8
                0x48, 0x8d, 0x3d, 0x00, 0x00, 0x00, 0x00, // lea  rdi, [rip + variable@TLSLD]
                0xe8, 0x00, 0x00, 0x00, 0x00,             // call __tls_get_addr
                0x8b, 0x80, 0x00, 0x00, 0x00, 0x00,       // mov  eax, [rax + variable@DTPOFF]
                0x48, 0x83, 0xc4, 0x08,                   // add  rsp, 8
                0xc3);                                    // ret
            builder.Relocate(text, 7, RelocX64.TlsLd, "variable", -4);
            builder.Relocate(text, 12, RelocX64.Plt32, "__tls_get_addr", -4);
            builder.Relocate(text, 18, RelocX64.DtpOff32, "variable");
        }
        else
        {
            text = builder.Text(".text",
                0x48, 0x83, 0xec, 0x08,                   // sub  rsp, 8
                0x48, 0x8d, 0x3d, 0x00, 0x00, 0x00, 0x00, // lea  rdi, [rip + variable@TLSLD]
                0x90,                                     // nop  (breaks the canonical sequence)
                0xe8, 0x00, 0x00, 0x00, 0x00,             // call __tls_get_addr
                0x8b, 0x80, 0x00, 0x00, 0x00, 0x00,       // mov  eax, [rax + variable@DTPOFF]
                0x48, 0x83, 0xc4, 0x08,                   // add  rsp, 8
                0xc3);                                    // ret
            builder.Relocate(text, 7, RelocX64.TlsLd, "variable", -4);
            builder.Relocate(text, 13, RelocX64.Plt32, "__tls_get_addr", -4);
            builder.Relocate(text, 19, RelocX64.DtpOff32, "variable");
        }

        builder.Global(function, text);
        if (defineVariable)
        {
            builder.Global("variable", ThreadLocalData(builder, 34), value: 4, type: SymbolType.Tls);
        }

        return builder.Build();
    }

    /// <summary>A .tdata section holding the 32-bit values 11 and <paramref name="value"/>.</summary>
    private static ElfObjectBuilder.Section ThreadLocalData(ElfObjectBuilder builder, int value) =>
        builder.AddSection(".tdata", [0x0b, 0, 0, 0, (byte)value, 0, 0, 0], SectionFlags.Alloc | SectionFlags.Write | SectionFlags.Tls, alignment: 4);
}
