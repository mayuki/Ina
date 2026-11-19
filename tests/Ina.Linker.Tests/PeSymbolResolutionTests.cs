using Ina.Linker.Pe;
using Ina.Linker.Tests.Support;

namespace Ina.Linker.Tests;

/// <summary>How the PE linker decides what a symbol refers to: libraries, COMDAT sections, weak and alternate names.</summary>
public class PeSymbolResolutionTests : PeLinkerTestBase
{
    [Fact]
    public void PullsLibraryMembersThatDefineUndefinedSymbols()
    {
        // The second member refers to a symbol that exists nowhere; it must not be loaded because nothing needs it.
        var library = ArchiveBuilder.BuildMicrosoft(
            (@"obj\release\helper.obj", WindowsCode.Function("helper", WindowsCode.Return(5))),
            (@"obj\release\unused.obj", WindowsCode.Function("unused", [0xe8, 0x00, 0x00, 0x00, 0x00, 0xc3])));

        Assert.Equal(5, Run(Link(WindowsCode.MainCalling("helper"), library)).ExitCode);
    }

    [Fact]
    public void LibraryMembersCanPullInFurtherMembers()
    {
        var library = ArchiveBuilder.BuildMicrosoft(
            ("leaf.obj", WindowsCode.Function("leaf", WindowsCode.Return(6))),
            ("middle.obj", CallerOf("middle", "leaf")));

        Assert.Equal(6, Run(Link(WindowsCode.MainCalling("middle"), library)).ExitCode);
    }

    [Fact]
    public void LibraryWithoutSymbolIndexIsIndexedFromItsMembers()
    {
        // GNU and LLVM tools can write COFF libraries as well, and their index may be missing.
        var library = ArchiveBuilder.Build(withSymbolTable: false, ("helper.obj", WindowsCode.Function("helper", WindowsCode.Return(8))));

        Assert.Equal(8, Run(Link(WindowsCode.MainCalling("helper"), library)).ExitCode);
    }

    [Fact]
    public void ImportLibrariesAreRejectedWithAnExplanation()
    {
        // A short import object: this is what the members of an import library such as kernel32.lib look like.
        byte[] import = [0x00, 0x00, 0xff, 0xff, 0x00, 0x00, 0x64, 0x86, .. new byte[12], .. "Mystery\0mystery.dll\0"u8];
        var library = ArchiveBuilder.BuildMicrosoft(("mystery.dll:Mystery,__imp_Mystery", import));

        var error = Assert.Throws<LinkerException>(() => Link(WindowsCode.MainCalling("Mystery"), library));

        Assert.Contains("import libraries are not supported", error.Message);
        Assert.Contains("mystery.dll", error.Message);
    }

    [Fact]
    public void FirstComdatSectionWinsAndDuplicatesAreDropped()
    {
        // Inline functions and templates are compiled into every object that uses them.
        var output = Link(WindowsCode.MainCalling("pick"), ComdatFunction("pick", 1, "KEPT-COPY"), ComdatFunction("pick", 2, "DROPPED-COPY"));

        // What is associated with the dropped copy goes with it.
        var image = Load(output);
        Assert.True(image.Contains("KEPT-COPY"u8));
        Assert.False(image.Contains("DROPPED-COPY"u8));
        Assert.Equal(1, Run(output).ExitCode);
    }

    [Fact]
    public void ComdatSectionThatForbidsDuplicatesReportsThem()
    {
        var error = Assert.Throws<LinkerException>(() => Link(
            WindowsCode.MainCalling("pick"), ComdatFunction("pick", 1, "A", CoffComdat.NoDuplicates), ComdatFunction("pick", 2, "B", CoffComdat.NoDuplicates)));

        Assert.Contains("duplicate symbol: pick", error.Message);
    }

    [Fact]
    public void LargestComdatSectionIsChosenWhenRequested()
    {
        static byte[] Table(params byte[] content)
        {
            var builder = new CoffObjectBuilder();
            var section = builder.ReadOnlyData(".rdata", content, 4);
            builder.Comdat(section, CoffComdat.Largest);
            builder.External("table", section);
            return builder.Build();
        }

        var program = new CoffObjectBuilder();
        var text = program.Text(".text$mn", 0x8b, 0x05, 0x00, 0x00, 0x00, 0x00, 0xc3); // mov eax, [rip + table] ; ret
        program.External("wmain", text);
        program.Relocate(text, 2, RelAmd64.Rel32, "table");

        Assert.Equal(2, Run(Link(program.Build(), Table(1, 0, 0, 0), Table(2, 0, 0, 0, 0, 0, 0, 0))).ExitCode);
    }

    [Fact]
    public void UnreferencedComdatSectionsAreDroppedButOrdinarySectionsAreKept()
    {
        var builder = new CoffObjectBuilder();
        builder.External("wmain", builder.Text(".text$mn", WindowsCode.Return(0)));
        var unused = builder.ReadOnlyData(".rdata", "UNUSED-FUNCTION-LEVEL-DATA"u8);
        builder.Comdat(unused);
        builder.External("unused", unused);
        builder.ReadOnlyData(".rdata", "ORDINARY-DATA"u8);

        var collected = Load(Link(builder.Build()));
        var everything = Load(Link(static o => o with { GarbageCollectSections = false }, builder.Build()));

        Assert.False(collected.Contains("UNUSED-FUNCTION-LEVEL-DATA"u8));
        Assert.True(collected.Contains("ORDINARY-DATA"u8));
        Assert.True(everything.Contains("UNUSED-FUNCTION-LEVEL-DATA"u8));
    }

    [Fact]
    public void WeakExternalFallsBackToItsDefault()
    {
        Assert.Equal(3, Run(Link(WindowsCode.MainCalling("hook"), WeakHook())).ExitCode);
    }

    [Fact]
    public void DefinitionReplacesTheDefaultOfAWeakExternal()
    {
        var output = Link(WindowsCode.MainCalling("hook"), WeakHook(), WindowsCode.Function("hook", WindowsCode.Return(4)));

        Assert.Equal(4, Run(output).ExitCode);
    }

    [Fact]
    public void AlternateNameIsUsedWhenNothingDefinesTheSymbol()
    {
        var builder = new CoffObjectBuilder();
        builder.External("fallback", builder.Text(".text$mn", WindowsCode.Return(6)));
        builder.Directive("/alternatename:feature_level=fallback");

        Assert.Equal(6, Run(Link(WindowsCode.MainCalling("feature_level"), builder.Build())).ExitCode);

        // A real definition wins over the alternate name, no matter where it comes from.
        var library = ArchiveBuilder.BuildMicrosoft(("feature.obj", WindowsCode.Function("feature_level", WindowsCode.Return(7))));
        Assert.Equal(7, Run(Link(WindowsCode.MainCalling("feature_level"), builder.Build(), library)).ExitCode);
    }

    [Fact]
    public void IncludeOptionOfAnObjectPullsTheSymbolFromALibrary()
    {
        var builder = new CoffObjectBuilder();
        builder.External("wmain", builder.Text(".text$mn", WindowsCode.Return(0)));
        builder.Directive("/include:forced");
        var library = ArchiveBuilder.BuildMicrosoft(("forced.obj", MarkedFunction("forced", "FORCED-MEMBER")));

        Assert.True(Load(Link(builder.Build(), library)).Contains("FORCED-MEMBER"u8));
    }

    [Fact]
    public void CommonSymbolsShareOneZeroInitializedVariable()
    {
        static byte[] Tentative(uint size)
        {
            var builder = new CoffObjectBuilder();
            builder.Common("shared", size);
            return builder.Build();
        }

        var program = new CoffObjectBuilder();
        var text = program.Text(".text$mn",
            0xc7, 0x05, 0x0c, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00, // mov dword ptr [rip + shared + 12], 9
            0x8b, 0x05, 0x0c, 0x00, 0x00, 0x00,                         // mov eax, [rip + shared + 12]
            0xc3);                                                      // ret
        program.External("wmain", text);
        program.Relocate(text, 2, RelAmd64.Rel32_4, "shared");
        program.Relocate(text, 12, RelAmd64.Rel32, "shared");

        const uint Megabyte = 0x10_0000;
        var output = Link(o => o with { ExportedSymbols = ["shared"] }, program.Build(), Tentative(4), Tentative(Megabyte));

        // The larger request wins, and the storage takes no room in the file.
        var image = Load(output);
        var data = image.SectionOf(image.Exports["shared"]);
        Assert.Equal(".data", data.Name);
        Assert.True(image.Exports["shared"] + Megabyte <= data.Rva + data.VirtualSize);
        Assert.True(new FileInfo(output).Length < Megabyte);
        Assert.Equal(9, Run(output).ExitCode);
    }

    [Fact]
    public void AbsoluteSymbolsKeepTheirValue()
    {
        var constants = new CoffObjectBuilder();
        constants.Absolute("magic", 0x1234);
        var program = new CoffObjectBuilder();
        var text = program.Text(".text$mn", 0xb8, 0x00, 0x00, 0x00, 0x00, 0xc3); // mov eax, magic ; ret
        program.External("wmain", text);
        program.Relocate(text, 1, RelAmd64.Addr32, "magic");

        // The value is not an address, so it is fine in an image that can be moved.
        Assert.Equal(0x1234, Run(Link(program.Build(), constants.Build())).ExitCode);
    }

    [Fact]
    public void DuplicateDefinitionsAreReported()
    {
        var error = Assert.Throws<LinkerException>(() => Link(
            WindowsCode.MainReturning(0), WindowsCode.Function("twice", WindowsCode.Return(1)), WindowsCode.Function("twice", WindowsCode.Return(2))));

        Assert.Contains("duplicate symbol: twice", error.Message);
        Assert.Contains("input1", error.Message);
        Assert.Contains("input2", error.Message);
    }

    [Fact]
    public void UndefinedSymbolsAreReportedWithTheirReferrer()
    {
        var error = Assert.Throws<LinkerException>(() => Link(WindowsCode.MainCalling("nowhere_to_be_found")));

        Assert.Contains("undefined symbol: nowhere_to_be_found", error.Message);
        Assert.Contains("referenced by input0:(.text$mn)", error.Message);
    }

    [Fact]
    public void MissingMainFunctionIsReportedByTheStartupCode()
    {
        var error = Assert.Throws<LinkerException>(() => Link(WindowsCode.Function("not_main", WindowsCode.Return(0))));

        Assert.Contains("undefined symbol: wmain", error.Message);
        Assert.Contains("<ina startup>", error.Message);
    }

    [Fact]
    public void FunctionsOfSystemLibrariesAreImportedFromTheLibraryThatExportsThem()
    {
        var builder = new CoffObjectBuilder();
        var text = builder.Text(".text$mn",
            0x48, 0x83, 0xec, 0x28,       // sub  rsp, 0x28
            0xe8, 0x00, 0x00, 0x00, 0x00, // call GetCurrentProcessId
            0xe8, 0x00, 0x00, 0x00, 0x00, // call GetTickCount64
            0x33, 0xc9,                   // xor  ecx, ecx
            0xe8, 0x00, 0x00, 0x00, 0x00, // call CoTaskMemFree       ; freeing a null pointer does nothing
            0xb8, 0x07, 0x00, 0x00, 0x00, // mov  eax, 7
            0x48, 0x83, 0xc4, 0x28,       // add  rsp, 0x28
            0xc3);                        // ret
        builder.External("wmain", text);
        builder.Relocate(text, 5, RelAmd64.Rel32, "GetCurrentProcessId");
        builder.Relocate(text, 10, RelAmd64.Rel32, "GetTickCount64");
        builder.Relocate(text, 17, RelAmd64.Rel32, "CoTaskMemFree");

        var output = Link(builder.Build());

        var imports = Load(output).Imports;
        Assert.Equal(["GetCurrentProcessId", "GetTickCount64"], imports.Single(static i => i.Library == "KERNEL32.dll").Names);
        Assert.Equal(["CoTaskMemFree"], imports.Single(static i => i.Library == "ole32.dll").Names);

        // Only what is used is imported: the C runtime for the startup code, and the two libraries above.
        Assert.All(imports, static i => Assert.True(i.Library is "KERNEL32.dll" or "ole32.dll" || i.Library.StartsWith("api-ms-win-crt-", StringComparison.Ordinal), i.Library));
        Assert.Equal(7, Run(output).ExitCode);
    }

    private static byte[] CallerOf(string name, string callee)
    {
        var builder = new CoffObjectBuilder();
        var text = builder.Text(".text$mn", WindowsCode.CallAndReturn);
        builder.External(name, text);
        builder.Relocate(text, WindowsCode.CallOperand, RelAmd64.Rel32, callee);
        return builder.Build();
    }

    /// <summary>
    /// A function in a COMDAT section, the way compilers emit inline functions, together with a data section that is
    /// associated with it and holds <paramref name="marker"/>.
    /// </summary>
    private static byte[] ComdatFunction(string name, int result, string marker, byte selection = CoffComdat.Any)
    {
        var builder = new CoffObjectBuilder();
        var text = builder.Text(".text$mn", WindowsCode.Return(result));
        builder.Comdat(text, selection);
        builder.External(name, text);
        builder.Associate(builder.ReadOnlyData(".rdata", System.Text.Encoding.ASCII.GetBytes(marker)), text);
        return builder.Build();
    }

    private static byte[] MarkedFunction(string name, string marker)
    {
        var builder = new CoffObjectBuilder();
        builder.External(name, builder.Text(".text$mn", WindowsCode.Return(0)));
        builder.ReadOnlyData(".rdata", System.Text.Encoding.ASCII.GetBytes(marker));
        return builder.Build();
    }

    /// <summary>An object that declares <c>hook</c> as a weak external whose default returns 3.</summary>
    private static byte[] WeakHook()
    {
        var builder = new CoffObjectBuilder();
        builder.Static("default_hook", builder.Text(".text$mn", WindowsCode.Return(3)));
        builder.WeakExternal("hook", "default_hook");
        return builder.Build();
    }
}
