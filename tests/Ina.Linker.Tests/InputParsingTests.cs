using System.Text;
using Ina.Linker.Abi;
using Ina.Linker.Elf;
using Ina.Linker.Input;
using Ina.Linker.Tests.Support;

namespace Ina.Linker.Tests;

public class InputParsingTests
{
    [Fact]
    public void ObjectFileExposesAllocatedSectionsSymbolsAndRelocations()
    {
        var builder = new ElfObjectBuilder();
        var text = builder.Text(".text", Code.CallAndReturn);
        builder.Global("entry", text, size: 14);
        builder.Local("local_label", text, value: 4);
        builder.Relocate(text, Code.CallOperand, RelocX64.Plt32, "callee", -4);
        builder.AddSection(".comment", "not loaded"u8, flags: 0);

        var file = ObjectFile.Parse("test.o", builder.Build(), static _ => true);

        var section = Assert.Single(file.Sections, static s => s is not null)!;
        Assert.Equal(".text", section.Name);
        Assert.Equal(Code.CallAndReturn, section.Content.ToArray());

        var relocation = Assert.Single(section.Relocations);
        Assert.Equal((ulong)Code.CallOperand, relocation.Offset);
        Assert.Equal(RelocX64.Plt32, relocation.Type);
        Assert.Equal(-4, relocation.Addend);
        Assert.Equal("callee", file.ElfSymbols[relocation.SymbolIndex].Name);
        Assert.True(file.ElfSymbols[relocation.SymbolIndex].IsUndefined);

        // Locals precede globals, and the boundary is recorded.
        var names = file.ElfSymbols.Select(static s => s.Name).ToList();
        Assert.True(names.IndexOf("local_label") < file.FirstGlobalSymbol);
        Assert.True(names.IndexOf("entry") >= file.FirstGlobalSymbol);
    }

    [Fact]
    public void SecondCopyOfAComdatGroupIsDiscarded()
    {
        var builder = new ElfObjectBuilder();
        var inline = builder.Text(".text.inline", Code.Return(1));
        builder.Global("inline", inline);
        builder.Comdat("inline", inline);
        var data = builder.Build();
        var claimed = new HashSet<string>();

        var first = ObjectFile.Parse("first.o", data, claimed.Add);
        var second = ObjectFile.Parse("second.o", data, claimed.Add);

        Assert.Contains(first.Sections, static s => s?.Name == ".text.inline");
        Assert.DoesNotContain(second.Sections, static s => s?.Name == ".text.inline");
    }

    [Theory]
    [InlineData("not an elf file at all, just text")]
    [InlineData("\u007fELF but far too short")]
    public void MalformedObjectsAreRejected(string content)
    {
        Assert.Throws<LinkerException>(() => ObjectFile.Parse("bad.o", Encoding.Latin1.GetBytes(content), static _ => true));
    }

    [Fact]
    public void ArchiveMemberIsHandedOutOnlyOnce()
    {
        var archive = ArchiveFile.Parse("lib.a", ArchiveBuilder.Build(
            withSymbolTable: true,
            ("first.o", Code.Function("alpha", Code.Return(1))),
            ("second_with_a_very_long_file_name.o", Code.Function("beta", Code.Return(2)))));

        Assert.True(archive.TryTakeMember("beta", out var name, out var data));
        Assert.Equal("second_with_a_very_long_file_name.o", name);
        Assert.True(ObjectFile.IsElf(data.Span));

        Assert.False(archive.TryTakeMember("beta", out _, out _));
        Assert.False(archive.TryTakeMember("gamma", out _, out _));
        Assert.Equal(["first.o", "second_with_a_very_long_file_name.o"], archive.Members().Select(static m => m.Name));
    }

    [Fact]
    public void ImportTableParsesLibrariesVersionsAndKinds()
    {
        var table = ImportLibrarySet.Parse(new StringReader("""
            # comment
            target demo libc
            interp /lib/ld-demo.so.1
            needed libm.so.6 libc.so.6
            lib libc.so.6
            ver GLIBC_2.2.5
            F abort write
            D stdout
            ver -
            F unversioned
            lib libm.so.6
            ver GLIBC_2.29
            F pow
            F write
            """.ReplaceLineEndings("\n").Replace("            ", string.Empty)));

        Assert.Equal("demo libc", table.Description);
        Assert.Equal("/lib/ld-demo.so.1", table.Interpreter);
        Assert.Equal(["libm.so.6", "libc.so.6"], table.Libraries);

        Assert.True(table.TryGet("stdout", out var stdout));
        Assert.Equal(new ImportedSymbol("libc.so.6", "GLIBC_2.2.5", ImportedSymbolKind.Object), stdout);
        Assert.True(table.TryGet("unversioned", out var unversioned));
        Assert.Null(unversioned.Version);
        Assert.True(table.TryGet("pow", out var pow));
        Assert.Equal("libm.so.6", pow.Library);

        // The first library that lists a symbol provides it.
        Assert.True(table.TryGet("write", out var write));
        Assert.Equal("libc.so.6", write.Library);
        Assert.False(table.TryGet("missing", out _));
    }

    [Theory]
    [InlineData(TargetArchitecture.X64, "/lib64/ld-linux-x86-64.so.2", "GLIBC_2.2.5", "GLIBC_2.14")]
    [InlineData(TargetArchitecture.Arm64, "/lib/ld-linux-aarch64.so.1", "GLIBC_2.17", "GLIBC_2.17")]
    public void EmbeddedGlibcTablesDescribeTheBaseline(TargetArchitecture architecture, string interpreter, string baseVersion, string memcpyVersion)
    {
        var table = ImportLibrarySet.ForGlibc(architecture);

        Assert.Equal(interpreter, table.Interpreter);
        Assert.StartsWith("glibc 2.27", table.Description);
        Assert.True(table.TryGet("__libc_start_main", out var start));
        Assert.Equal(new ImportedSymbol("libc.so.6", baseVersion, ImportedSymbolKind.Function), start);
        Assert.True(table.TryGet("memcpy", out var memcpy));
        Assert.Equal(memcpyVersion, memcpy.Version);
        Assert.True(table.TryGet("stderr", out var stderr));
        Assert.Equal(ImportedSymbolKind.Object, stderr.Kind);

        // Before glibc 2.34 these lived in separate libraries, which must therefore be listed as dependencies.
        Assert.True(table.TryGet("pthread_create", out var pthreadCreate));
        Assert.Equal("libpthread.so.0", pthreadCreate.Library);
        Assert.True(table.TryGet("dlopen", out var dlopen));
        Assert.Equal("libdl.so.2", dlopen.Library);

        // Not exported by the shared library; the linker generates them instead.
        Assert.False(table.TryGet("atexit", out _));
    }

    [Fact]
    public void AbiTableGeneratorRecordsDefaultVersionsOfExportedSymbols()
    {
        const string libc = "/lib/x86_64-linux-gnu/libc.so.6";
        Assert.SkipUnless(File.Exists(libc), "Needs the C library of an x86-64 glibc system as sample input.");

        var writer = new StringWriter();
        AbiTableGenerator.Generate(writer, "host libc", "/lib64/ld-linux-x86-64.so.2", [("libc.so.6", File.ReadAllBytes(libc))], ["libc.so.6"]);
        var table = ImportLibrarySet.Parse(new StringReader(writer.ToString()));

        Assert.True(table.TryGet("printf", out var printf));
        Assert.Equal(new ImportedSymbol("libc.so.6", "GLIBC_2.2.5", ImportedSymbolKind.Function), printf);

        // Of the two versions of memcpy only the default one is recorded.
        Assert.True(table.TryGet("memcpy", out var memcpy));
        Assert.Equal("GLIBC_2.14", memcpy.Version);
        Assert.True(table.TryGet("stdout", out var stdout));
        Assert.Equal(ImportedSymbolKind.Object, stdout.Kind);

        // Symbols private to the C library are not part of its interface.
        Assert.DoesNotContain(table.Symbols, static s => s.Value.Version?.EndsWith("_PRIVATE", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData(".text", ".text")]
    [InlineData(".text.unlikely.foo", ".text")]
    [InlineData(".textual", ".textual")]
    [InlineData(".data.rel.ro.local", ".data.rel.ro")]
    [InlineData(".data.rel", ".data")]
    [InlineData(".rodata.str1.1", ".rodata")]
    [InlineData(".tbss.var", ".tbss")]
    [InlineData(".init_array.00100", ".init_array")]
    [InlineData("__managedcode", "__managedcode")]
    [InlineData(".dotnet_eh_table", ".dotnet_eh_table")]
    public void InputSectionsMapToOutputSectionsByPrefix(string input, string expected)
    {
        Assert.Equal(expected, SectionNaming.OutputName(input));
    }

    [Theory]
    [InlineData("__managedcode", true)]
    [InlineData("registry_1", true)]
    [InlineData(".text", false)]
    [InlineData("1abc", false)]
    [InlineData("", false)]
    public void StartStopSymbolsExistOnlyForIdentifierLikeNames(string name, bool expected)
    {
        Assert.Equal(expected, SectionNaming.IsCIdentifier(name));
    }
}
