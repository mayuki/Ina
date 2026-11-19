using Ina.Linker.Abi;
using Ina.Linker.MachO;
using Ina.Linker.Tests.Support;

namespace Ina.Linker.Tests;

/// <summary>Checks the structure of the produced Mach-O image that the loader, the kernel and the tools depend on.</summary>
public class MachOImageLayoutTests : MachOLinkerTestBase
{
    private const string SystemLibrary = "/usr/lib/libSystem.B.dylib";
    private const uint AdHocLinkerSigned = 0x20002;

    [Fact]
    public void ImageIsAPositionIndependentExecutableForTheLoaderOfMacOS()
    {
        var image = Load(Link(Function("_main", 0x52800540, Ret)));

        Assert.Equal(MachCpu.Arm64, image.CpuType);
        Assert.Equal(MachHeader.FileTypeExecute, image.FileType);
        Assert.Equal(MachHeader.NoUndefs | MachHeader.DyldLink | MachHeader.TwoLevel | MachHeader.Pie, image.Flags);
        Assert.Equal("/usr/lib/dyld", image.DynamicLoader);
        Assert.Equal(image.Symbols["_main"], image.EntryAddress);
    }

    [Fact]
    public void ImageAlwaysDependsOnTheCLibrary()
    {
        // The program does not call anything, but the loader needs the C library to set up the process.
        var image = Load(Link(Function("_main", 0x52800540, Ret)));

        var library = Assert.Single(image.Libraries);
        Assert.Equal(SystemLibrary, library.Name);
        Assert.False(library.IsWeak);
        Assert.Equal(new Version(1, 0, 0), library.CompatibilityVersion);
    }

    [Fact]
    public void OnlyLibrariesThatProvideAnImportAreListedAsDependencies()
    {
        var image = Load(Link(MainCalling("_objc_autoreleasePoolPush")));

        Assert.Equal([SystemLibrary, "/usr/lib/libobjc.A.dylib"], image.Libraries.Select(static library => library.Name));
        var bind = Assert.Single(image.Binds);
        Assert.Equal("/usr/lib/libobjc.A.dylib", image.LibraryOf(bind));
        Assert.False(bind.IsWeakImport);
    }

    [Fact]
    public void BuildVersionStatesTheMinimumSystemOfTheImportTable()
    {
        var image = Load(Link(Function("_main", 0x52800540, Ret)));
        var table = ImportLibrarySet.ForMacOS(TargetArchitecture.Arm64);

        Assert.Equal(1u, image.Platform);
        Assert.Equal(new Version(12, 0, 0), image.MinimumOSVersion);
        Assert.Equal(new Version(table.SdkVersion!.Major, table.SdkVersion.Minor, 0), image.SdkVersion);
    }

    [Fact]
    public void SegmentsFollowTheConventionsOfThePlatform()
    {
        var image = Load(Link(ProgramWithAllKindsOfData()));

        Assert.Equal(["__PAGEZERO", "__TEXT", "__DATA_CONST", "__DATA", "__LINKEDIT"], image.Segments.Select(static segment => segment.Name));

        // The first 4 GiB are unmapped, so that truncated pointers fault.
        var zero = image.GetSegment("__PAGEZERO");
        Assert.Equal((0UL, 0x1_0000_0000UL, 0UL, 0u), (zero.Address, zero.VmSize, zero.FileSize, zero.InitialProtection));

        var text = image.GetSegment("__TEXT");
        Assert.Equal((0x1_0000_0000UL, 0UL), (text.Address, text.FileOffset));
        Assert.Equal(SegmentProtection.Read | SegmentProtection.Execute, text.InitialProtection);
        Assert.Equal(SegmentProtection.Read | SegmentProtection.Write, image.GetSegment("__DATA").InitialProtection);
        Assert.Equal(SegmentProtection.Read, image.GetSegment("__LINKEDIT").InitialProtection);
        Assert.Equal(SegmentProtection.FlagReadOnly, image.GetSegment("__DATA_CONST").Flags);

        // Apple silicon uses 16 KiB pages; segments are mapped page by page and follow each other without gaps.
        var mapped = image.Segments.Skip(1).ToList();
        Assert.All(mapped, static segment =>
        {
            Assert.Equal(0UL, segment.Address % 0x4000);
            Assert.Equal(0UL, segment.FileOffset % 0x4000);
            Assert.Equal(0UL, segment.VmSize % 0x4000);
            Assert.True(segment.FileSize <= segment.VmSize);
        });
        for (var i = 1; i < mapped.Count; i++)
        {
            Assert.Equal(mapped[i - 1].Address + mapped[i - 1].VmSize, mapped[i].Address);
            Assert.Equal(mapped[i - 1].FileOffset + mapped[i - 1].FileSize, mapped[i].FileOffset);
        }
    }

    [Fact]
    public void SectionsAreAlignedAndZeroFilledOnesEndTheirSegment()
    {
        var image = Load(Link(ProgramWithAllKindsOfData()));

        Assert.All(image.Sections, static section => Assert.Equal(0UL, section.Address % section.Alignment));
        var data = image.GetSegment("__DATA").Sections;
        Assert.Equal(["__data", "__bss"], data.Select(static section => section.Name));
        Assert.Equal(MachSectionType.ZeroFill, data[1].Type);
        Assert.Equal(0u, data[1].FileOffset);

        // The header and the load commands are the start of the text segment; the code follows them.
        var text = image.GetSegment("__TEXT");
        Assert.Equal("__text", text.Sections[0].Name);
        Assert.True(text.Sections[0].Address > text.Address);
        Assert.Equal(text.Sections[0].Address - text.Address, text.Sections[0].FileOffset);
    }

    [Fact]
    public void ConstantDataMovesToTheSegmentThatBecomesReadOnlyAfterLoading()
    {
        var image = Load(Link(ProgramWithAllKindsOfData()));

        // The pointer table is only written by the loader, like the GOT.
        Assert.Equal(["__got", "__const"], image.GetSegment("__DATA_CONST").Sections.Select(static section => section.Name));
        var table = image.GetSection("__DATA_CONST", "__const");
        Assert.Contains(table.Address, image.Rebases);
        Assert.Equal(image.GetSection("__TEXT", "__cstring").Address, image.U64(table.Address));
    }

    [Fact]
    public void AbsoluteAddressInTheTextSegmentIsRejected()
    {
        var builder = new MachObjectBuilder();
        builder.Global("_main", builder.Text(Code.Arm64(0x52800540, Ret)));
        var table = builder.Const(new byte[8]);
        builder.Local("_table", table, desc: MachSymbolType.NoDeadStrip);
        builder.Pointer(table, 0, "_main");

        var error = Assert.Throws<LinkerException>(() => Link(builder.Build()));

        Assert.Contains("read-only section", error.Message);
        Assert.Contains("_main", error.Message);
    }

    [Fact]
    public void StubsJumpThroughGotEntriesThatAreBoundWhenTheImageIsLoaded()
    {
        var image = Load(Link(MachOExecutableTests.PrintMessageProgram()));
        var stubs = image.GetSection("__TEXT", "__stubs");
        var got = image.GetSection("__DATA_CONST", "__got");
        Assert.Equal(MachSectionType.SymbolStubs, stubs.Type);
        Assert.Equal(12u, stubs.Reserved2);
        Assert.Equal(MachSectionType.NonLazySymbolPointers, got.Type);

        // bl _puts goes to the stub: adrp x16, slot@PAGE ; ldr x16, [x16, slot@PAGEOFF] ; br x16
        var stub = BranchTarget(image, image.Symbols["_main"] + 12);
        Assert.Equal(stubs.Address, stub);
        var slot = AdrpTarget(image, stub) + Imm12(image, stub + 4) * 8;
        Assert.Equal(got.Address, slot);
        Assert.Equal(0xd61f0200u, image.U32(stub + 8));

        var bind = Assert.Single(image.Binds);
        Assert.Equal((slot, "_puts", 0L, false), (bind.Address, bind.SymbolName, bind.Addend, bind.IsWeakImport));
        Assert.Equal(SystemLibrary, image.LibraryOf(bind));
        Assert.Empty(image.Rebases);

        // Tools find out what a stub or a GOT entry stands for through the indirect symbol table.
        var puts = image.SymbolTable.FindIndex(static symbol => symbol.Name == "_puts");
        Assert.Equal([(uint)puts, (uint)puts], image.IndirectSymbols);
        Assert.Equal((0u, 1u), (stubs.Reserved1, got.Reserved1));
        Assert.Equal(1, image.SymbolTable[puts].LibraryOrdinal);
    }

    [Fact]
    public void GotLoadOfASymbolOfTheImageIsRewrittenToComputeTheAddress()
    {
        var builder = new MachObjectBuilder();
        builder.Global("_value", builder.Data([0x11, 0, 0, 0]));
        var text = builder.Text(Code.Arm64(
            0x90000008,  // adrp x8, _value@GOTPAGE
            0xf9400108,  // ldr  x8, [x8, _value@GOTPAGEOFF]
            0xb9400100,  // ldr  w0, [x8]
            Ret));
        builder.Global("_main", text);
        builder.Arm64(text, 0, MachRelocArm64.GotLoadPage21, "_value");
        builder.Arm64(text, 4, MachRelocArm64.GotLoadPageOff12, "_value");
        var output = Link(builder.Build());

        // The load from the GOT became "add x8, x8, #offset": one memory access and one fixup less.
        var image = Load(output);
        var main = image.Symbols["_main"];
        Assert.Null(image.FindSection("__DATA_CONST", "__got"));
        Assert.Equal(0x91000108u, image.U32(main + 4) & 0xffc003ff);
        Assert.Equal(image.Symbols["_value"], AdrpTarget(image, main) + Imm12(image, main + 4));
        Assert.Equal(0x11, Run(output).ExitCode);
    }

    [Fact]
    public void PointerToAnImportedSymbolIsBoundInPlace()
    {
        var builder = new MachObjectBuilder();
        builder.Global("_main", builder.Text(Code.Arm64(0x52800540, Ret)));
        var data = builder.Data([8, 0, 0, 0, 0, 0, 0, 0]);
        builder.Local("_pointer", data, desc: MachSymbolType.NoDeadStrip);
        builder.Pointer(data, 0, "___stdoutp"); // the field holds 8, which is added to the address

        var image = Load(Link(builder.Build()));

        var bind = Assert.Single(image.Binds);
        Assert.Equal((image.Symbols["_pointer"], "___stdoutp", 8L), (bind.Address, bind.SymbolName, bind.Addend));
        Assert.Null(image.FindSection("__DATA_CONST", "__got"));
    }

    [Fact]
    public void SignatureCoversTheImageAndIsAdHoc()
    {
        var output = Link(MachOExecutableTests.PrintMessageProgram());
        var image = Load(output);

        // Apple silicon only runs signed code. An ad-hoc signature has no identity, just the hashes of the pages.
        var signature = image.Signature!;
        Assert.True(signature.HashesMatch);
        Assert.Equal(Path.GetFileName(output), signature.Identifier);
        Assert.Equal(AdHocLinkerSigned, signature.Flags);
        var text = image.GetSegment("__TEXT");
        Assert.Equal((text.FileOffset, text.FileSize), (signature.ExecutableSegmentBase, signature.ExecutableSegmentLimit));

        // The signature is the last thing in the file and in __LINKEDIT.
        var linkEdit = image.GetSegment("__LINKEDIT");
        Assert.Equal((ulong)new FileInfo(output).Length, linkEdit.FileOffset + linkEdit.FileSize);
        Assert.Equal(LoadCommand.CodeSignature, image.Commands[^1]);
    }

    [Fact]
    public void SymbolTableListsDefinedSymbolsOnlyOnRequest()
    {
        var program = MachOExecutableTests.PrintMessageProgram();
        var with = Load(Link(program));
        var without = Load(Link(TargetArchitecture.Arm64, static o => o with { EmitSymbolTable = false }, program));

        Assert.Equal(1, with.LocalSymbolCount);
        Assert.Equal(MachSymbolType.Section, with.SymbolTable[0].Type);
        Assert.Equal(with.GetSection("__TEXT", "__text").Address, with.Symbols["_main"]);

        // Imports stay: the stubs and the GOT are described in terms of them.
        Assert.Equal(0, without.LocalSymbolCount);
        Assert.Equal(["_puts"], without.UndefinedSymbols);
        Assert.Equal(["_puts"], with.UndefinedSymbols);
    }

    [Fact]
    public void ExportedSymbolsAreExternalAndInTheExportTrie()
    {
        var builder = new MachObjectBuilder();
        builder.Global("_main", builder.Text(Code.Arm64(0x52800540, Ret)));
        builder.Global("_exported_data", builder.Data([1, 2, 3, 4]));
        builder.Global("_exported_too", builder.AddSection("__DATA", "__other", [5, 6, 7, 8], alignmentLog2: 2));
        builder.Global("_private_data", builder.AddSection("__DATA", "__private", [9, 9, 9, 9], alignmentLog2: 2));

        var image = Load(Link(TargetArchitecture.Arm64, static o => o with { ExportedSymbols = ["exported_data", "exported_too", "not_defined"] }, builder.Build()));

        // Exported symbols are kept although nothing refers to them; the others are neither kept nor visible.
        Assert.Equal(["_exported_data", "_exported_too"], image.Exports.Keys.Order());
        Assert.Equal(image.Symbols["_exported_data"], image.Exports["_exported_data"]);
        Assert.Equal(image.Symbols["_exported_too"], image.Exports["_exported_too"]);
        Assert.Equal(2, image.ExportedSymbolCount);
        Assert.DoesNotContain("_private_data", image.Symbols.Keys);
        Assert.All(image.SymbolTable.Where(static symbol => symbol.Name.StartsWith("_exported", StringComparison.Ordinal)), static symbol => Assert.True(symbol.IsExternal));
    }

    [Fact]
    public void FunctionStartsListTheFunctionsOfTheImage()
    {
        var builder = new MachObjectBuilder();
        var text = builder.Text(Code.Arm64(0x52800540, Ret, StpFrame, 0x94000000, LdpFrame, Ret));
        builder.Global("_main", text, value: 8);
        builder.Global("_helper", text);
        builder.Arm64(text, 12, MachRelocArm64.Branch26, "_helper");

        var image = Load(Link(builder.Build()));

        Assert.Equal([image.Symbols["_helper"], image.Symbols["_main"]], image.FunctionStarts);
    }

    [Fact]
    public void OutputIsReproducible()
    {
        var program = MachOExecutableTests.PrintMessageProgram();
        var first = Link(program);
        var second = Link(program);
        var other = Link(MachOExecutableTests.PrintMessageProgram("another message"));

        // The names differ (out0, out1), so everything but the identifier in the signature must be the same.
        var firstImage = Load(first);
        var secondImage = Load(second);
        Assert.Equal(firstImage.Uuid, secondImage.Uuid);
        var signedSize = (int)firstImage.Signature!.CodeLimit;
        Assert.Equal(File.ReadAllBytes(first).AsSpan(0, signedSize).ToArray(), File.ReadAllBytes(second).AsSpan(0, signedSize).ToArray());

        // The UUID is derived from the content: it identifies a build, for example in crash reports.
        Assert.Equal(16, firstImage.Uuid!.Length);
        Assert.NotEqual(firstImage.Uuid, Load(other).Uuid);
    }

    [Fact]
    public void FixedAddressExecutablesAreNotSupported()
    {
        var error = Assert.Throws<LinkerException>(() => Link(TargetArchitecture.Arm64, static o => o with { PositionIndependent = false }, Function("_main", Ret)));

        Assert.Contains("position independent", error.Message);
    }

    [Fact]
    public void ExistingOutputIsReplacedRatherThanOverwritten()
    {
        // The kernel remembers the signature of a file it has run; writing into that file would get the new program killed.
        var builder = new MachObjectBuilder();
        builder.Global("_main", builder.Text(Code.Arm64(0x52800540, Ret)));
        var output = Link(builder.Build());
        Assert.Equal(42, Run(output).ExitCode);

        MachOLinker.Link(new LinkOptions
        {
            OutputPath = output,
            Architecture = TargetArchitecture.Arm64,
            Imports = ImportLibrarySet.ForMacOS(TargetArchitecture.Arm64),
            Inputs = [new LinkInput("main.o", Function("_main", 0x528000e0, Ret))], // mov w0, #7
        });

        Assert.Equal(7, Run(output).ExitCode);
    }

    /// <summary>A program with code, a string, a pointer table that is constant after loading, data and zero-filled data.</summary>
    private static byte[] ProgramWithAllKindsOfData()
    {
        var builder = new MachObjectBuilder();
        var message = builder.CString("text\0"u8);
        var table = builder.AddSection("__DATA", "__const", new byte[8], alignmentLog2: 3);
        builder.Pointer(table, 0, message);
        builder.Local("_table", table);
        var data = builder.Data([1, 0, 0, 0]);
        builder.Local("_data", data);
        var zeroed = builder.ZeroFill("__bss", 64);
        builder.Local("_zeroed", zeroed);
        var text = builder.Text(Code.Arm64(
            StpFrame,
            0x90000008,  // adrp x8, _table@PAGE
            0xf9400100,  // ldr  x0, [x8, _table@PAGEOFF]
            0x94000000,  // bl   _puts
            0x90000008,  // adrp x8, _data@PAGE
            0xb9400100,  // ldr  w0, [x8, _data@PAGEOFF]
            0x90000008,  // adrp x8, _zeroed@PAGE
            0xb9000100,  // str  w0, [x8, _zeroed@PAGEOFF]
            LdpFrame,
            Ret));
        builder.Global("_main", text);
        builder.Arm64(text, 4, MachRelocArm64.Page21, "_table");
        builder.Arm64(text, 8, MachRelocArm64.PageOff12, "_table");
        builder.Arm64(text, 12, MachRelocArm64.Branch26, "_puts");
        builder.Arm64(text, 16, MachRelocArm64.Page21, "_data");
        builder.Arm64(text, 20, MachRelocArm64.PageOff12, "_data");
        builder.Arm64(text, 24, MachRelocArm64.Page21, "_zeroed");
        builder.Arm64(text, 28, MachRelocArm64.PageOff12, "_zeroed");
        return builder.Build();
    }
}
