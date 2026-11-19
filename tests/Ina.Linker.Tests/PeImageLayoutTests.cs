using System.Text;
using Ina.Linker.Elf;
using Ina.Linker.Pe;
using Ina.Linker.Tests.Support;

namespace Ina.Linker.Tests;

/// <summary>The structure of the Windows executables that the linker writes; nothing here needs to run them.</summary>
public class PeImageLayoutTests : PeLinkerTestBase
{
    [Fact]
    public void HeadersDescribeAConsoleProgramForX64()
    {
        var image = Load(Link(WindowsCode.MainReturning(0)));

        Assert.Equal(0x8664, image.Machine);
        Assert.Equal(0x0022, image.Characteristics); // executable, large address aware
        Assert.Equal(3, image.Subsystem);            // console
        Assert.Equal(0x8160, image.DllCharacteristics);
        Assert.Equal(0x1_4000_0000ul, image.ImageBase);

        // NativeAOT reserves 1.5 MiB for the stack of the main thread.
        Assert.Equal(1572864ul, image.StackReserve);
    }

    [Fact]
    public void SectionsFollowTheConventionalOrderAndAlignment()
    {
        var builder = WindowsCode.MainPrinting("table");
        var table = builder.Data(".data", new byte[8]);
        builder.External("table", table);
        builder.Relocate(table, 0, RelAmd64.Addr64, builder.ReadOnlyData(".rdata", "text\0"u8));

        var image = Load(Link(builder.Build()));

        Assert.Equal([".text", ".rdata", ".data", ".tls", ".pdata", ".reloc"], image.Sections.Select(static s => s.Name));
        Assert.Equal(0x1000u, image.SectionAlignment);
        Assert.Equal(0x200u, image.FileAlignment);
        Assert.All(image.Sections, section =>
        {
            Assert.Equal(0u, section.Rva % image.SectionAlignment);
            Assert.Equal(0u, section.FileOffset % image.FileAlignment);
            Assert.Equal(0u, section.RawSize % image.FileAlignment);
        });

        // Sections follow each other without gaps in memory, and the image ends after the last one.
        for (var i = 1; i < image.Sections.Count; i++)
        {
            var previous = image.Sections[i - 1];
            Assert.Equal((previous.Rva + previous.VirtualSize + 0xfff) & ~0xfffu, image.Sections[i].Rva);
        }

        Assert.Equal((image.Sections[^1].Rva + image.Sections[^1].VirtualSize + 0xfff) & ~0xfffu, image.SizeOfImage);

        // Code is executable and not writable, constants are read-only, data is writable and not executable.
        Assert.Equal(0x6000_0020u, image.FindSection(".text")!.Characteristics);
        Assert.Equal(0x4000_0040u, image.FindSection(".rdata")!.Characteristics);
        Assert.Equal(0xc000_0040u, image.FindSection(".data")!.Characteristics);
        Assert.Equal(".text", image.SectionOf(image.EntryPoint).Name);
    }

    [Fact]
    public void ImportTablesLiveInReadOnlyDataAndTheLoaderKnowsWhereTheAddressesAre()
    {
        var image = Load(Link(WindowsCode.MainReturning(0)));

        var (directory, _) = image.Directory(PeImage.DirectoryImport);
        var (addresses, size) = image.Directory(PeImage.DirectoryImportAddressTable);
        Assert.Equal(".rdata", image.SectionOf(directory).Name);
        Assert.Equal(".rdata", image.SectionOf(addresses).Name);

        // One slot per imported symbol plus a terminator per library.
        Assert.Equal((uint)(image.Imports.Sum(static i => i.Names.Count + 1) * 8), size);

        // The startup code needs these to run the initializers, pass the command line and exit.
        var runtime = image.Imports.Single(static i => i.Library == "api-ms-win-crt-runtime-l1-1-0.dll").Names;
        Assert.Equal(["__p___argc", "__p___wargv", "_configure_wide_argv", "_initterm", "_initterm_e", "_set_app_type", "exit"], runtime);
    }

    [Fact]
    public void ChunksAreOrderedByTheSuffixOfTheirSectionNameAndMergedOnRequest()
    {
        // The runtime finds tables by a start and an end marker: the parts of a section are sorted by what
        // follows the dollar sign, across all objects.
        var markers = new CoffObjectBuilder();
        AddPart(markers, ".table$Z", "<end>", "table_end");
        AddPart(markers, ".table$A", "<start>", "table_start");
        markers.Directive("/merge:.table=.rdata");
        var entries = new CoffObjectBuilder();
        AddPart(entries, ".table$M", "second", null);
        AddPart(entries, ".table$C", "first,", null);

        var output = Link(o => o with { ExportedSymbols = ["table_start", "table_end"] }, WindowsCode.MainReturning(0), markers.Build(), entries.Build());

        var image = Load(output);
        Assert.Null(image.FindSection(".table"));
        var start = image.Exports["table_start"];
        Assert.Equal(".rdata", image.SectionOf(start).Name);
        Assert.Equal("<start>first,second<end>", Encoding.ASCII.GetString(image.Read(start, 24)));
        Assert.Equal(start + 19, image.Exports["table_end"]);

        static void AddPart(CoffObjectBuilder builder, string name, string content, string? symbol)
        {
            var section = builder.ReadOnlyData(name, Encoding.ASCII.GetBytes(content));
            if (symbol is not null)
            {
                builder.External(symbol, section);
            }
        }
    }

    [Fact]
    public void MergedSectionsOfTheLinkerOptionsApplyToChainsOfSections()
    {
        // NativeAOT stores data that is expanded at startup in a section of its own, which is folded into the
        // zero-initialized data, which in turn is part of the data section.
        var builder = new CoffObjectBuilder();
        builder.External("wmain", builder.Text(".text$mn", WindowsCode.Return(0)));
        builder.External("expanded", builder.Bss("hydrated", 0x3000));
        builder.External("managed", builder.Text(".managedcode$I", WindowsCode.Return(1)));

        var output = Link(
            o => o with
            {
                ExportedSymbols = ["expanded", "managed"],
                MergedSections = new Dictionary<string, string> { [".managedcode"] = ".text", ["hydrated"] = ".bss" },
            },
            builder.Build());

        var image = Load(output);
        Assert.Equal(".data", image.SectionOf(image.Exports["expanded"]).Name);
        Assert.Equal(".text", image.SectionOf(image.Exports["managed"]).Name);
        Assert.DoesNotContain(image.Sections, static s => s.Name is "hydrated" or ".bss" or ".managed");
    }

    [Fact]
    public void FunctionTableIsSortedByAddress()
    {
        // Two functions whose unwind entries are listed in the opposite order of the code.
        var builder = new CoffObjectBuilder();
        var text = builder.Text(".text$mn", [.. WindowsCode.Return(0), .. new byte[10], .. WindowsCode.Return(1)]);
        builder.External("wmain", text);
        var unwind = builder.ReadOnlyData(".xdata", [0x01, 0x00, 0x00, 0x00], 4);
        var entries = new byte[24];
        LittleEndian.U32(entries, 0, 16);  // second function: begin
        LittleEndian.U32(entries, 4, 22);  //                  end
        LittleEndian.U32(entries, 12, 0);  // first function:  begin
        LittleEndian.U32(entries, 16, 6);  //                  end
        var table = builder.ReadOnlyData(".pdata", entries, 4);
        foreach (var offset in new uint[] { 0, 4, 12, 16 })
        {
            builder.Relocate(table, offset, RelAmd64.Addr32Nb, text);
        }

        builder.Relocate(table, 8, RelAmd64.Addr32Nb, unwind);
        builder.Relocate(table, 20, RelAmd64.Addr32Nb, unwind);

        var image = Load(Link(builder.Build()));

        // The two functions above, plus the startup code and the thread-local initialization of the linker.
        var (rva, size) = image.Directory(PeImage.DirectoryException);
        Assert.Equal(".pdata", image.SectionOf(rva).Name);
        Assert.Equal(4u * 12, size);
        var begins = Enumerable.Range(0, 4).Select(i => image.U32(rva + (uint)i * 12)).ToList();
        Assert.Equal(begins.Order(), begins);
        Assert.All(Enumerable.Range(0, 4), i =>
        {
            var begin = image.U32(rva + (uint)i * 12);
            var end = image.U32(rva + (uint)i * 12 + 4);
            Assert.True(begin < end);
            Assert.Equal(".text", image.SectionOf(begin).Name);
            Assert.Equal(".rdata", image.SectionOf(image.U32(rva + (uint)i * 12 + 8)).Name);
        });
    }

    [Fact]
    public void ExportTableListsTheRequestedSymbolsByName()
    {
        var builder = new CoffObjectBuilder();
        builder.External("wmain", builder.Text(".text$mn", WindowsCode.Return(0)));
        builder.External("RuntimeDescriptor", builder.ReadOnlyData(".rdata", "descriptor"u8, 8));
        builder.External("AnotherSymbol", builder.Data(".data", "another"u8));

        // An object can ask for an export itself; this is how the runtime publishes its descriptor.
        builder.Directive("/EXPORT:AnotherSymbol,DATA");

        var output = Link(o => o with { ExportedSymbols = ["RuntimeDescriptor"] }, builder.Build());

        var image = Load(output);
        Assert.Equal(["AnotherSymbol", "RuntimeDescriptor"], image.Exports.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("descriptor", Encoding.ASCII.GetString(image.Read(image.Exports["RuntimeDescriptor"], 10)));
        Assert.Equal("another", Encoding.ASCII.GetString(image.Read(image.Exports["AnotherSymbol"], 7)));

        // The directory names the module after the file.
        var (directory, _) = image.Directory(PeImage.DirectoryExport);
        Assert.Equal(Path.GetFileName(output), image.CString(image.U32(directory + 12)));
    }

    [Fact]
    public void ExportOfAnUndefinedSymbolIsAnError()
    {
        var error = Assert.Throws<LinkerException>(() => Link(o => o with { ExportedSymbols = ["Missing"] }, WindowsCode.MainReturning(0)));

        Assert.Contains("undefined symbol: Missing", error.Message);
    }

    [Fact]
    public void ResourcesBecomeTheResourceDirectory()
    {
        // The compiler emits the resources of the application as a directory section and a data section; an entry
        // of the directory refers to the data by its address relative to the image base.
        var builder = new CoffObjectBuilder();
        builder.External("wmain", builder.Text(".text$mn", WindowsCode.Return(0)));
        var directory = builder.ReadOnlyData(".rsrc$01", new byte[16], 4);
        var data = builder.ReadOnlyData(".rsrc$02", "version info"u8, 4);
        builder.Relocate(directory, 8, RelAmd64.Addr32Nb, data);

        var image = Load(Link(builder.Build()));

        var section = image.FindSection(".rsrc")!;
        Assert.Equal((section.Rva, section.VirtualSize), image.Directory(PeImage.DirectoryResource));
        Assert.Equal(0x4000_0040u, section.Characteristics);
        Assert.Equal("version info", Encoding.ASCII.GetString(image.Read(image.U32(section.Rva + 8), 12)));
    }

    [Fact]
    public void ImageBaseSymbolDenotesTheStartOfTheImage()
    {
        // Compiled code computes image-relative addresses at run time with this symbol.
        var builder = new CoffObjectBuilder();
        builder.External("wmain", builder.Text(".text$mn", WindowsCode.Return(0)));
        var slot = builder.Data(".data", new byte[8]);
        builder.External("base", slot);
        builder.Relocate(slot, 0, RelAmd64.Addr64, "__ImageBase");

        var image = Load(Link(o => o with { ExportedSymbols = ["base"] }, builder.Build()));

        Assert.Equal(image.ImageBase, image.U64(image.Exports["base"]));
        Assert.Contains(image.Exports["base"], image.BaseRelocations);
    }

    [Fact]
    public void OutputIsReproducible()
    {
        var first = File.ReadAllBytes(Link(o => o with { OutputPath = Path.Combine(Path.GetDirectoryName(o.OutputPath)!, "a", "program.exe") }, WindowsCode.MainReturning(1)));
        var second = File.ReadAllBytes(Link(o => o with { OutputPath = Path.Combine(Path.GetDirectoryName(o.OutputPath)!, "b", "program.exe") }, WindowsCode.MainReturning(1)));
        var different = File.ReadAllBytes(Link(o => o with { OutputPath = Path.Combine(Path.GetDirectoryName(o.OutputPath)!, "c", "program.exe") }, WindowsCode.MainReturning(2)));

        Assert.Equal(first, second);

        // The time stamp is derived from the content instead of the clock.
        Assert.NotEqual(0u, new PeImage(first).TimeDateStamp);
        Assert.NotEqual(new PeImage(first).TimeDateStamp, new PeImage(different).TimeDateStamp);
    }

    [Fact]
    public void LinkResultReportsWhatWasLinked()
    {
        var output = Path.Combine(Path.GetTempPath(), "ina-tests", Guid.NewGuid().ToString("N"), "nested", "program.exe");
        try
        {
            var result = PeLinker.Link(new PeLinkOptions
            {
                OutputPath = output,
                Architecture = TargetArchitecture.X64,
                Imports = Abi.ImportLibrarySet.ForWindows(),
                Inputs = [new LinkInput("main.obj", WindowsCode.MainReturning(0))],
            });

            Assert.Equal(new FileInfo(output).Length, result.FileSize);
            Assert.Contains("api-ms-win-crt-runtime-l1-1-0.dll", result.NeededLibraries);
            Assert.Contains("exit", result.ImportedSymbols);

            // Windows binds by name only; there are no symbol versions to require.
            Assert.Empty(result.RequiredVersions);
            Assert.True(result.ObjectCount >= 2);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(output))!, recursive: true);
        }
    }
}
