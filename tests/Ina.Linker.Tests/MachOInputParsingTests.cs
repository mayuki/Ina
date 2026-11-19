using System.Text;
using Ina.Linker.Abi;
using Ina.Linker.Input;
using Ina.Linker.MachO;
using Ina.Linker.Tests.Support;

namespace Ina.Linker.Tests;

/// <summary>Reading the inputs of a Mach-O link: object files, static libraries and the import tables.</summary>
public class MachOInputParsingTests
{
    private const string SystemLibrary = "/usr/lib/libSystem.B.dylib";

    [Fact]
    public void ObjectFileIsSplitIntoAtomsAtItsSymbols()
    {
        var builder = new MachObjectBuilder();
        var text = builder.Text(Code.Arm64(0x94000000, 0xd65f03c0, 0xd65f03c0, 0xd65f03c0));
        builder.Global("_entry", text);
        builder.Local("_local", text, value: 8);
        builder.Local("Ltmp1", text, value: 12); // an assembler-local label is a position, not a piece of its own
        builder.Arm64(text, 0, MachRelocArm64.Branch26, "_callee", addend: 4);
        builder.AddSection("__DWARF", "__debug_info", "not loaded"u8, MachSectionType.AttrDebug);

        var file = MachObjectFile.Parse("test.o", builder.Build());

        Assert.Equal(MachCpu.Arm64, file.CpuType);
        Assert.True(file.HasSubsections);
        Assert.Equal(12u << 16, file.MinimumOSVersion);
        Assert.Equal(MachSectionKind.Ignored, file.Sections.Single(static section => section.SectionName == "__debug_info").Kind);

        var section = file.Sections.Single(static section => section.SectionName == "__text");
        Assert.Equal([(0UL, 8UL), (8UL, 8UL)], section.Atoms.Select(static atom => (atom.InputOffset, atom.Size)));

        // The addend of an instruction is a relocation entry of its own in the file; here it is part of the reference.
        var relocation = Assert.Single(section.Atoms[0].Relocations);
        Assert.Equal((0u, MachRelocKind.Branch, 4L), (relocation.Offset, relocation.Kind, relocation.Addend));
        Assert.Equal("_callee", file.Nlist[relocation.SymbolIndex].Name);
        Assert.True(file.Nlist[relocation.SymbolIndex].IsUndefined);
        Assert.Empty(section.Atoms[1].Relocations);
    }

    [Fact]
    public void ReferenceToASectionBecomesAReferenceToTheAtomAtThatAddress()
    {
        var builder = new MachObjectBuilder();
        var data = builder.Data([1, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0, 4, 0, 0, 0]);
        builder.Local("_first", data);
        builder.Local("_second", data, value: 8);
        var table = builder.AddSection("__DATA", "__table", new byte[8], alignmentLog2: 3);
        builder.Pointer(table, 0, data, addend: 12);

        var file = MachObjectFile.Parse("test.o", builder.Build());

        // The relocation names the section and the field holds an address; the linker needs to know which atom that is.
        var relocation = Assert.Single(file.Sections.Single(static section => section.SectionName == "__table").Atoms[0].Relocations);
        var anchor = file.Nlist[relocation.SymbolIndex];
        Assert.Equal(string.Empty, anchor.Name);
        Assert.Equal(file.Sections.Single(static section => section.SectionName == "__data").Address + 8, anchor.Value);
        Assert.Equal(4L, relocation.Addend);
    }

    [Fact]
    public void StringLiteralsAreAtomsOfTheirOwn()
    {
        var builder = new MachObjectBuilder();
        builder.CString("one\0two\0three\0"u8);

        var file = MachObjectFile.Parse("test.o", builder.Build());

        Assert.Equal([4UL, 4UL, 6UL], file.Sections[0].Atoms.Select(static atom => atom.Size));
    }

    [Fact]
    public void AtomKeepsTheAlignmentItsPositionHad()
    {
        var builder = new MachObjectBuilder();
        var data = builder.Data(new byte[32], alignmentLog2: 4);
        builder.Local("_a", data);
        builder.Local("_b", data, value: 4);
        builder.Local("_c", data, value: 8);
        builder.Local("_d", data, value: 16);

        var file = MachObjectFile.Parse("test.o", builder.Build());

        Assert.Equal([16UL, 4UL, 8UL, 16UL], file.Sections[0].Atoms.Select(static atom => atom.Alignment));
    }

    [Fact]
    public void UnwindEntriesAreAttachedToTheCodeTheyDescribe()
    {
        var builder = new MachObjectBuilder();
        var text = builder.Text(Code.Arm64(0xd65f03c0, 0xd65f03c0, 0xd65f03c0));
        builder.Global("_first", text);
        builder.Global("_second", text, value: 4);
        var tables = builder.AddSection("__TEXT", "__gcc_except_tab", new byte[8], alignmentLog2: 2);
        builder.UnwindEntry(text, 4, 8, 0x44000000, "___objc_personality_v0", tables, 4);

        var file = MachObjectFile.Parse("test.o", builder.Build());

        var code = file.Sections.Single(static section => section.SectionName == "__text");
        Assert.Null(code.Atoms[0].UnwindRecords);
        var record = Assert.Single(code.Atoms[1].UnwindRecords!);
        Assert.Equal((0u, 8u, 0x44000000u), (record.Offset, record.Length, record.Encoding));
        Assert.Equal("___objc_personality_v0", file.Nlist[record.PersonalityIndex].Name);
        Assert.Equal("__gcc_except_tab", record.Lsda!.Section.SectionName);
        Assert.Equal(4u, record.LsdaOffset);
        Assert.Equal(MachSectionKind.CompactUnwind, file.Sections.Single(static section => section.SegmentName == "__LD").Kind);
    }

    [Theory]
    [InlineData("not a mach-o file at all, just some text")]
    [InlineData("Ïúíþ but far too short")]
    public void MalformedObjectsAreRejected(string content)
    {
        Assert.Throws<LinkerException>(() => MachObjectFile.Parse("bad.o", Encoding.Latin1.GetBytes(content)));
    }

    [Fact]
    public void LinkedImageIsNotAnInput()
    {
        var builder = new MachObjectBuilder();
        builder.Global("_main", builder.Text(Code.Arm64(0xd65f03c0)));
        var image = builder.Build();
        image[12] = (byte)MachHeader.FileTypeExecute;

        var error = Assert.Throws<LinkerException>(() => MachObjectFile.Parse("app", image));

        Assert.Contains("not a relocatable object file", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BsdArchiveMemberIsHandedOutOnlyOnce(bool withSymbolTable)
    {
        var first = new MachObjectBuilder();
        first.Global("_alpha", first.Text(Code.Arm64(0xd65f03c0)));
        var second = new MachObjectBuilder();
        second.Global("_beta", second.Text(Code.Arm64(0xd65f03c0)));
        var archive = ArchiveFile.Parse(
            "lib.a",
            MachArchiveBuilder.Build(withSymbolTable, ("first.o", first.Build()), ("second_with_a_very_long_file_name.o", second.Build())),
            static (name, data) => MachObjectFile.Parse(name, data).Nlist.Where(static symbol => symbol.IsExternal && !symbol.IsUndefined).Select(static symbol => symbol.Name));

        Assert.True(archive.TryTakeMember("_beta", out var name, out var data));
        Assert.Equal("second_with_a_very_long_file_name.o", name);
        Assert.True(MachObjectFile.IsMachObject(data.Span));

        Assert.False(archive.TryTakeMember("_beta", out _, out _));
        Assert.False(archive.TryTakeMember("_gamma", out _, out _));
        Assert.Equal(["first.o", "second_with_a_very_long_file_name.o"], archive.Members().Select(static member => member.Name));
    }

    [Theory]
    [InlineData(TargetArchitecture.Arm64, "arm64")]
    [InlineData(TargetArchitecture.X64, "x86_64")]
    public void EmbeddedMacOSTablesDescribeTheSystemLibraries(TargetArchitecture architecture, string name)
    {
        var table = ImportLibrarySet.ForMacOS(architecture);

        Assert.StartsWith($"macOS 12.0 {name}", table.Description);
        Assert.Equal("/usr/lib/dyld", table.Interpreter);
        Assert.Equal(new Version(12, 0), table.MinimumOSVersion);
        Assert.NotNull(table.SdkVersion);
        Assert.Equal(SystemLibrary, table.Libraries[0]);
        Assert.Equal(new Version(1, 0, 0), table.LibraryVersion(SystemLibrary).Compatibility);

        // The C library is listed completely: compiled applications refer to it directly, for example for math.
        Assert.True(table.TryGet("_malloc", out var malloc));
        Assert.Equal(new ImportedSymbol(SystemLibrary, null, ImportedSymbolKind.Unspecified), malloc);
        foreach (var symbol in new[] { "_atanh", "_pthread_create", "_dlopen", "__tlv_bootstrap", "___stdoutp", "__availability_version_check" })
        {
            Assert.True(table.TryGet(symbol, out var import), symbol);
            Assert.Equal(SystemLibrary, import.Library);
        }

        // Of the frameworks, what the native libraries of the .NET runtime use.
        Assert.True(table.TryGet("_CFRelease", out var release));
        Assert.EndsWith("/CoreFoundation", release.Library);
        Assert.True(table.TryGet("_OBJC_CLASS_$_NSString", out var nsString));
        Assert.EndsWith("/Foundation", nsString.Library);
        Assert.True(table.TryGet("_objc_msgSend", out var msgSend));
        Assert.Equal("/usr/lib/libobjc.A.dylib", msgSend.Library);

        // The Swift part of Foundation was a library of its own up to macOS 13, and that is the oldest system an
        // executable has to run on. Newer systems keep the library and forward to the framework.
        Assert.True(table.TryGet("_$s10Foundation4DataVN", out var data));
        Assert.Equal("/usr/lib/swift/libswiftFoundation.dylib", data.Library);

        // Not exported by any library; the linker generates them instead.
        Assert.False(table.TryGet("___isPlatformVersionAtLeast", out _));
    }

    [Fact]
    public void MacOSTableIsGeneratedFromTextBasedStubs()
    {
        const string system = """
            --- !tapi-tbd
            tbd-version:     4
            targets:         [ x86_64-macos, arm64e-macos ]
            install-name:    '/usr/lib/libSystem.B.dylib'
            current-version: 1356
            reexported-libraries:
              - targets:         [ x86_64-macos, arm64e-macos ]
                libraries:       [ '/usr/lib/system/libsystem_c.dylib' ]
            exports:
              - targets:         [ x86_64-macos ]
                symbols:         [ _only_on_intel ]
              - targets:         [ x86_64-macos, arm64e-macos ]
                symbols:         [ _top_level, '$ld$previous$/usr/lib/libold.dylib$$1$10.9$10.15$_top_level$' ]
            --- !tapi-tbd
            tbd-version:     4
            targets:         [ x86_64-macos, arm64e-macos ]
            install-name:    '/usr/lib/system/libsystem_c.dylib'
            current-version: 1700
            exports:
              - targets:         [ x86_64-macos, arm64e-macos ]
                symbols:         [ _abort, _printf,
                                   _zeta ]
                thread-local-symbols: [ _per_thread ]
            ...
            """;
        const string framework = """
            --- !tapi-tbd
            tbd-version:     4
            targets:         [ x86_64-macos, arm64-macos, arm64e-macos ]
            install-name:    '/System/Library/Frameworks/Demo.framework/Versions/A/Demo'
            current-version: 12.5.1
            compatibility-version: 2
            exports:
              - targets:         [ arm64-macos, arm64e-macos ]
                symbols:         [ _DemoUsed, _DemoUnused, _abort ]
                objc-classes:    [ DemoObject, DemoOther ]
                weak-symbols:    [ '_$s4Demo4UsedV' ]
            ...
            """;

        // The runtime library refers to some symbols of the framework; nothing else of it is listed.
        var runtime = new MachObjectBuilder();
        var text = runtime.Text(Code.Arm64(0x94000000, 0x94000000, 0x94000000, 0xd65f03c0));
        runtime.Global("_RuntimeFunction", text);
        runtime.Arm64(text, 0, MachRelocArm64.Branch26, "_DemoUsed");
        runtime.Arm64(text, 4, MachRelocArm64.Branch26, "_$s4Demo4UsedV");
        runtime.Arm64(text, 8, MachRelocArm64.Branch26, "_ProvidedByNobody");
        var pointer = runtime.Data(new byte[8]);
        runtime.Pointer(pointer, 0, "_OBJC_CLASS_$_DemoObject");

        var writer = new StringWriter();
        var missing = MacOSAbiTableGenerator.Generate(
            writer, TargetArchitecture.Arm64, new Version(12, 0), new Version(26, 5),
            [new MacOSLibraryStub(system.ReplaceLineEndings("\n"), Complete: true), new MacOSLibraryStub(framework.ReplaceLineEndings("\n"), Complete: false)],
            ["/System/Library/Frameworks/Demo.framework/Versions/A/Demo", SystemLibrary],
            [new LinkInput("libRuntime.a", MachArchiveBuilder.Build(withSymbolTable: true, ("runtime.o", runtime.Build())))]);
        var table = ImportLibrarySet.Parse(new StringReader(writer.ToString()));

        Assert.Equal("macOS 12.0 arm64 (SDK 26.5)", table.Description);
        Assert.Equal((new Version(12, 0), new Version(26, 5)), (table.MinimumOSVersion, table.SdkVersion));
        Assert.Equal(["/System/Library/Frameworks/Demo.framework/Versions/A/Demo", SystemLibrary], table.Libraries);
        Assert.Equal((new Version(2, 0, 0), new Version(12, 5, 1)), table.LibraryVersion("/System/Library/Frameworks/Demo.framework/Versions/A/Demo"));
        Assert.Equal((new Version(1, 0, 0), new Version(1356, 0, 0)), table.LibraryVersion(SystemLibrary));

        // Symbols of a re-exported library count as symbols of the library that re-exports it.
        Assert.Equal(
            ["_abort", "_per_thread", "_printf", "_top_level", "_zeta"],
            table.Symbols.Where(static symbol => symbol.Value.Library == SystemLibrary).Select(static symbol => symbol.Key).Order(StringComparer.Ordinal));
        Assert.True(table.TryGet("_per_thread", out var perThread));
        Assert.Equal(ImportedSymbolKind.Tls, perThread.Kind);
        Assert.False(table.TryGet("_only_on_intel", out _));

        Assert.Equal(
            ["_$s4Demo4UsedV", "_DemoUsed", "_OBJC_CLASS_$_DemoObject"],
            table.Symbols.Where(static symbol => symbol.Value.Library != SystemLibrary).Select(static symbol => symbol.Key).Order(StringComparer.Ordinal));
        Assert.Equal(["_ProvidedByNobody"], missing);
    }

    [Fact]
    public void SymbolIsAttributedToTheLibraryThatHasItOnTheOldestSupportedSystem()
    {
        // The framework absorbed a library: its stub lists the symbols, and says where they were before.
        const string framework = """
            --- !tapi-tbd
            tbd-version:     4
            targets:         [ x86_64-macos, arm64-macos ]
            install-name:    '/System/Library/Frameworks/Demo.framework/Versions/A/Demo'
            current-version: 5
            exports:
              - targets:         [ x86_64-macos, arm64-macos ]
                symbols:         [ '$ld$previous$/usr/lib/swift/libswiftDemo.dylib$2.0.0$1$10.15$13.0$_$s4Demo5MovedV$',
                                   '$ld$previous$/usr/lib/swift/libswiftDemo.dylib$2.0.0$6$13.0$16.0$_$s4Demo4HereV$',
                                   '$ld$previous$@rpath/libswiftDemo.dylib$1.0.0$1$10.9$10.15$_$s4Demo4HereV$',
                                   '$ld$previous$/usr/lib/libold.dylib$$1$10.9$10.15$$',
                                   '_$s4Demo5MovedV', '_$s4Demo4HereV' ]
            ...
            """;

        // A library that was split off from another one: everything it exports was in the other one before.
        const string split = """
            --- !tapi-tbd
            tbd-version:     4
            targets:         [ x86_64-macos, arm64-macos ]
            install-name:    '/usr/lib/swift/libswift_part.dylib'
            exports:
              - targets:         [ x86_64-macos, arm64-macos ]
                symbols:         [ '$ld$previous$/usr/lib/swift/libswiftWhole.dylib$$1$10.14.4$15.0$$', _part_symbol ]
            ...
            """;

        var writer = new StringWriter();
        MacOSAbiTableGenerator.Generate(
            writer, TargetArchitecture.Arm64, new Version(12, 0), new Version(26, 5),
            [new MacOSLibraryStub(framework.ReplaceLineEndings("\n"), Complete: true), new MacOSLibraryStub(split.ReplaceLineEndings("\n"), Complete: true)], [], []);
        var table = ImportLibrarySet.Parse(new StringReader(writer.ToString()));

        // Only the directive for macOS (platform 1) whose range contains the baseline applies.
        Assert.True(table.TryGet("_$s4Demo5MovedV", out var moved));
        Assert.Equal("/usr/lib/swift/libswiftDemo.dylib", moved.Library);
        Assert.Equal((new Version(2, 0, 0), new Version(2, 0, 0)), table.LibraryVersion(moved.Library));
        Assert.True(table.TryGet("_$s4Demo4HereV", out var here));
        Assert.Equal("/System/Library/Frameworks/Demo.framework/Versions/A/Demo", here.Library);
        Assert.True(table.TryGet("_part_symbol", out var part));
        Assert.Equal("/usr/lib/swift/libswiftWhole.dylib", part.Library);
        Assert.Equal(3, table.Count);
    }

    [Fact]
    public void MacOSTableKeywordsAreValidated()
    {
        Assert.Throws<LinkerException>(() => ImportLibrarySet.Parse(new StringReader("platform macos twelve 26.5\n")));
        Assert.Throws<LinkerException>(() => ImportLibrarySet.Parse(new StringReader("libver 1.0.0 2.0.0\n")));
        Assert.Throws<LinkerException>(() => ImportLibrarySet.Parse(new StringReader("S _symbol_without_library\n")));
    }
}
