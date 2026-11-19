using Ina.Linker.Abi;
using Ina.Linker.Pe;
using Ina.Linker.Tests.Support;

namespace Ina.Linker.Tests;

/// <summary>Reading COFF objects and the import table that stands in for the import libraries of Windows.</summary>
public class CoffInputTests : PeLinkerTestBase
{
    [Fact]
    public void ObjectIsReadBackWithSectionsSymbolsAndRelocations()
    {
        var builder = new CoffObjectBuilder();
        var text = builder.Text(".text$mn", WindowsCode.CallAndReturn);
        var data = builder.Data(".data_with_a_long_name", [1, 2, 3, 4], 4);
        var zero = builder.Bss(".bss", 64, 32);
        builder.External("a_function_with_a_long_name", text);
        builder.Static("local", data, 2);
        builder.Common("tentative", 24);
        builder.Absolute("constant", 99);
        builder.Relocate(text, WindowsCode.CallOperand, RelAmd64.Rel32, "elsewhere");

        var file = CoffObject.Parse("test.obj", builder.Build());

        Assert.Equal(CoffMachine.Amd64, file.Machine);
        var sections = file.Sections.OfType<CoffSection>().ToList();
        Assert.Equal([".text$mn", ".data_with_a_long_name", ".bss"], sections.Select(static s => s.Name));
        Assert.Equal([16u, 4u, 32u], sections.Select(static s => s.Alignment));
        Assert.Equal([14u, 4u, 64u], sections.Select(static s => s.Size));
        Assert.True(sections[2].IsUninitialized);

        var relocation = Assert.Single(sections[0].Relocations);
        Assert.Equal((5u, RelAmd64.Rel32, "elsewhere"), (relocation.Offset, relocation.Type, file.Records[relocation.SymbolIndex].Name));

        var symbols = file.Records.Where(static r => !r.IsAuxiliary).ToDictionary(static r => r.Name);
        Assert.Equal((1, true), (symbols["a_function_with_a_long_name"].SectionNumber, symbols["a_function_with_a_long_name"].IsExternal));
        Assert.Equal((2, 2u, false), (symbols["local"].SectionNumber, symbols["local"].Value, symbols["local"].IsExternal));
        Assert.True(symbols["tentative"].IsCommon);
        Assert.Equal(24u, symbols["tentative"].Value);
        Assert.True(symbols["constant"].IsAbsolute);
        Assert.True(symbols["elsewhere"].IsUndefined);
    }

    [Fact]
    public void BigObjectFormatIsUnderstood()
    {
        // Compilers switch to this format when an object has more sections than the regular header can count.
        var builder = new CoffObjectBuilder();
        var text = builder.Text(".text$mn", WindowsCode.Return(9));
        builder.Comdat(text);
        builder.External("wmain", text);
        var regular = builder.Build();
        var big = builder.Build(bigObj: true);

        Assert.NotEqual(regular.Length, big.Length);
        var file = CoffObject.Parse("big.obj", big);
        Assert.Equal(CoffComdat.Any, Assert.Single(file.Sections.OfType<CoffSection>()).ComdatSelection);
        Assert.Equal(9, Run(Link(big)).ExitCode);
    }

    [Fact]
    public void ComdatInformationComesFromTheSectionSymbols()
    {
        var builder = new CoffObjectBuilder();
        var function = builder.Text(".text$mn", WindowsCode.Return(0));
        builder.Comdat(function, CoffComdat.SameSize);
        builder.External("inline_function", function);
        var unwind = builder.ReadOnlyData(".xdata", new byte[8], 4);
        builder.Associate(unwind, function);
        var ordinary = builder.ReadOnlyData(".rdata", new byte[4]);

        var file = CoffObject.Parse("test.obj", builder.Build());

        var sections = file.Sections.OfType<CoffSection>().ToList();
        Assert.Equal(CoffComdat.SameSize, sections[0].ComdatSelection);
        Assert.Equal("inline_function", file.Records[sections[0].ComdatLeader].Name);
        Assert.Equal((CoffComdat.Associative, 1), (sections[1].ComdatSelection, sections[1].AssociatedSection));
        Assert.False(sections[2].IsComdat);
        Assert.NotNull(ordinary);
    }

    [Fact]
    public void LinkerOptionsOfAnObjectAreSplitLikeACommandLine()
    {
        var builder = new CoffObjectBuilder();
        builder.External("wmain", builder.Text(".text$mn", WindowsCode.Return(0)));
        builder.Directive("/DEFAULTLIB:LIBCMT");
        builder.Directive("/FAILIFMISMATCH:Runtime Library=MT_StaticRelease");
        builder.Directive("-export:wmain");

        var file = CoffObject.Parse("test.obj", builder.Build());

        // The section with the options is not linked.
        Assert.Equal(["/DEFAULTLIB:LIBCMT", "/FAILIFMISMATCH:Runtime Library=MT_StaticRelease", "-export:wmain"], file.Directives);
        Assert.Single(file.Sections.OfType<CoffSection>());

        // Options that select or check the libraries of the C++ compiler have no meaning for Ina and are ignored.
        Assert.Contains("wmain", Load(Link(builder.Build())).Exports.Keys);
    }

    [Fact]
    public void DebugInformationIsNotLinked()
    {
        var builder = new CoffObjectBuilder();
        builder.External("wmain", builder.Text(".text$mn", WindowsCode.Return(0)));
        builder.AddSection(".debug$S", "CODEVIEW-SYMBOLS"u8, CoffSectionFlags.InitializedData | CoffSectionFlags.Read | CoffSectionFlags.Discardable);

        Assert.False(Load(Link(builder.Build())).Contains("CODEVIEW-SYMBOLS"u8));
    }

    [Fact]
    public void InputForAnotherFormatOrArchitectureIsRejected()
    {
        var elf = Code.Function("main", Code.Return(0));
        var arm64 = new CoffObjectBuilder(CoffMachine.Arm64);
        arm64.External("helper", arm64.Text(".text", 0xc0, 0x03, 0x5f, 0xd6));

        Assert.Contains("unknown file format", Assert.Throws<LinkerException>(() => Link(elf)).Message);
        Assert.Contains("different architecture", Assert.Throws<LinkerException>(() => Link(WindowsCode.MainReturning(0), arm64.Build())).Message);
    }

    [Fact]
    public void UnsupportedRelocationIsReportedWithItsLocation()
    {
        var builder = new CoffObjectBuilder();
        var text = builder.Text(".text$mn", [0xb8, 0x00, 0x00, 0x00, 0x00, 0xc3]);
        builder.External("wmain", text);
        builder.Relocate(text, 1, 0x7f, text);

        var error = Assert.Throws<LinkerException>(() => Link(builder.Build()));

        Assert.Contains("input0:(.text$mn): unsupported relocation IMAGE_REL_AMD64 0x7f", error.Message);
    }

    [Fact]
    public void AbsoluteAddressThatDoesNotFitIsRejectedInMovableImages()
    {
        // A 32-bit absolute address can not be adjusted by the loader of a 64-bit image.
        var builder = new CoffObjectBuilder();
        var text = builder.Text(".text$mn", [0xb8, 0x00, 0x00, 0x00, 0x00, 0xc3]); // mov eax, offset wmain ; ret
        builder.External("wmain", text);
        builder.Relocate(text, 1, RelAmd64.Addr32, text);

        var error = Assert.Throws<LinkerException>(() => Link(builder.Build()));

        Assert.Contains("32-bit absolute address", error.Message);
    }

    [Fact]
    public void WindowsImportTableAttributesSymbolsToTheLibrariesThatExportThem()
    {
        var table = ImportLibrarySet.ForWindows();

        static string? LibraryOf(ImportLibrarySet table, string name) => table.TryGet(name, out var symbol) ? symbol.Library : null;

        Assert.Equal("KERNEL32.dll", LibraryOf(table, "CreateFileW"));
        Assert.Equal("ADVAPI32.dll", LibraryOf(table, "RegOpenKeyExW"));
        Assert.Equal("bcrypt.dll", LibraryOf(table, "BCryptGenRandom"));
        Assert.Equal("WS2_32.dll", LibraryOf(table, "WSAStartup"));
        Assert.Equal("ntdll.dll", LibraryOf(table, "RtlGetVersion"));

        // The futex-like wait functions are not exported by kernel32.dll; official binaries use this API set too.
        Assert.Equal("api-ms-win-core-synch-l1-2-0.dll", LibraryOf(table, "WaitOnAddress"));

        // The C runtime is addressed through its API sets, never through the private copies inside ntdll.dll.
        Assert.Equal("api-ms-win-crt-heap-l1-1-0.dll", LibraryOf(table, "malloc"));
        Assert.Equal("api-ms-win-crt-stdio-l1-1-0.dll", LibraryOf(table, "__acrt_iob_func"));
        Assert.Equal("api-ms-win-crt-private-l1-1-0.dll", LibraryOf(table, "memcpy"));
        Assert.All(table.Symbols.Where(static s => s.Value.Library == "ntdll.dll"), static s => Assert.Matches("^(Nt|Zw|Rtl|Ldr|Etw|Tp|Dbg)[A-Z]", s.Key));

        // Functions that only exist in the static C runtime are not importable; the linker provides them itself.
        Assert.Null(LibraryOf(table, "atexit"));
        Assert.Null(LibraryOf(table, "__security_check_cookie"));
        Assert.Null(LibraryOf(table, "wmainCRTStartup"));

        // Libraries are listed in the order of the official link line, which also is the import order.
        Assert.Equal(["ADVAPI32.dll", "bcrypt.dll", "CRYPT32.dll"], table.Libraries.Take(3));
    }
}
