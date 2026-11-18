using Ina.Linker.Abi;
using Ina.Linker.Elf;
using Ina.Linker.Tests.Support;

namespace Ina.Linker.Tests;

/// <summary>Checks the structure of the produced image that loaders and tools depend on.</summary>
public class ImageLayoutTests : LinkerTestBase
{
    [Fact]
    public void ImageRequestsTheDynamicLoaderOfTheTargetCLibrary()
    {
        var image = Load(Link(Code.Function("main", Code.Return(0))));

        Assert.Equal("/lib64/ld-linux-x86-64.so.2", image.Interpreter);
        Assert.Equal((ushort)ElfMachine.X86_64, image.Machine);
        Assert.Contains("libc.so.6", image.Needed);
        Assert.Contains("__libc_start_main", image.DynamicSymbols);
    }

    [Fact]
    public void OnlyLibrariesThatProvideAnImportAreListedAsDependencies()
    {
        var image = Load(Link(Code.Function("main", Code.Return(0))));
        Assert.DoesNotContain("libm.so.6", image.Needed);

        // cos lives in libm.so.6.
        var withMath = Load(Link(Code.MainCalling("cos")));
        Assert.Contains("libm.so.6", withMath.Needed);
        Assert.True(withMath.Needed.IndexOf("libm.so.6") < withMath.Needed.IndexOf("libc.so.6"));
    }

    [Fact]
    public void ImportsBindToTheVersionsOfTheBaselineCLibrary()
    {
        // memcpy has an old GLIBC_2.2.5 version and a newer GLIBC_2.14 one; a linker picks the default of its baseline.
        var output = Path.Combine(Path.GetTempPath(), "ina-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var result = ElfLinker.Link(new LinkOptions
            {
                OutputPath = output,
                Architecture = TargetArchitecture.X64,
                Imports = ImportLibrarySet.ForGlibc(TargetArchitecture.X64),
                Inputs = [new LinkInput("main.o", Code.MainCalling("memcpy"))],
            });

            Assert.Contains("libc.so.6:GLIBC_2.14", result.RequiredVersions);
            Assert.Contains("libc.so.6:GLIBC_2.2.5", result.RequiredVersions);
            Assert.Contains("memcpy", result.ImportedSymbols);

            // Nothing newer than the baseline can ever be required.
            Assert.All(result.RequiredVersions, static v => Assert.True(VersionOf(v) <= new Version(2, 27), v));
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void LoadableSegmentsArePageAlignedAndNeverWritableAndExecutable()
    {
        var image = Load(Link(static o => o with { GarbageCollectSections = false }, ProgramWithAllKindsOfSections()));
        var loads = image.Segments.Where(static s => s.Type == SegmentType.Load && s.MemorySize > 0).ToList();

        Assert.Equal(3, loads.Count);
        Assert.All(loads, static s =>
        {
            Assert.Equal(0x1000UL, s.Alignment);
            Assert.Equal(s.Offset % s.Alignment, s.Address % s.Alignment);
            Assert.False((s.Flags & SegmentFlags.Write) != 0 && (s.Flags & SegmentFlags.Execute) != 0);
        });

        // Segments do not overlap and appear in ascending order.
        for (var i = 1; i < loads.Count; i++)
        {
            Assert.True(loads[i].Address >= loads[i - 1].Address + loads[i - 1].MemorySize);
        }

        // The stack must not be executable.
        var stack = Assert.Single(image.Segments, static s => s.Type == SegmentType.GnuStack);
        Assert.Equal(SegmentFlags.Read | SegmentFlags.Write, stack.Flags);
    }

    [Fact]
    public void RelocationReadOnlyRegionCoversDynamicDataAndEndsOnAPageBoundary()
    {
        var image = Load(Link(static o => o with { GarbageCollectSections = false }, ProgramWithAllKindsOfSections()));
        var relro = Assert.Single(image.Segments, static s => s.Type == SegmentType.GnuRelRo);
        var end = relro.Address + relro.MemorySize;

        Assert.Equal(0UL, end % 0x1000);
        foreach (var name in new[] { ".dynamic", ".got", ".init_array" })
        {
            var section = image.GetSection(name);
            Assert.InRange(section.Address, relro.Address, end - 1);
            Assert.True(section.Address + section.Size <= end);
        }

        // Ordinary writable data stays writable.
        Assert.True(image.GetSection(".data").Address >= end);
        Assert.True(image.GetSection(".bss").Address >= end);
    }

    [Theory]
    [InlineData(TargetArchitecture.X64)]
    [InlineData(TargetArchitecture.Arm64)]
    public void RelocationReadOnlyRegionIsBackedByALoadableSegmentEvenInTinyImages(TargetArchitecture architecture)
    {
        // Without any ordinary data the protected region is the last thing in the image. The loader changes the
        // protection of the whole region, so all of it has to be mapped.
        var machine = architecture == TargetArchitecture.X64 ? ElfMachine.X86_64 : ElfMachine.AArch64;
        var code = architecture == TargetArchitecture.X64 ? Code.Return(0) : Code.Arm64(0x52800000, 0xd65f03c0);
        var builder = new ElfObjectBuilder(machine);
        builder.Global("main", builder.Text(".text", code));

        var image = Load(Link(o => o with { Architecture = architecture, Imports = ImportLibrarySet.ForGlibc(architecture) }, builder.Build()));
        var relro = Assert.Single(image.Segments, static s => s.Type == SegmentType.GnuRelRo);
        var load = Assert.Single(image.Segments, s => s.Type == SegmentType.Load && s.Address <= relro.Address && relro.Address < s.Address + s.MemorySize);

        Assert.True(relro.Address + relro.MemorySize <= load.Address + load.MemorySize);
        Assert.Equal(0UL, (relro.Address + relro.MemorySize) % load.Alignment);
    }

    [Fact]
    public void ZeroInitializedDataTakesNoSpaceInTheFile()
    {
        var builder = new ElfObjectBuilder();
        builder.Global("main", builder.Text(".text", Code.Return(0)));
        builder.Global("big", builder.NoBits(".bss", 16 * 1024 * 1024), type: SymbolType.Object);
        var output = Link(static o => o with { GarbageCollectSections = false }, builder.Build());

        Assert.True(new FileInfo(output).Length < 64 * 1024);
        Assert.Equal(16UL * 1024 * 1024, Load(output).GetSection(".bss").Size);
        Assert.Equal(0, Run(output).ExitCode);
    }

    [Fact]
    public void SectionsOfTheSameKindAreMergedByNamePrefix()
    {
        var builder = new ElfObjectBuilder();
        builder.Global("main", builder.Text(".text.main", Code.Return(0)));
        builder.Global("a", builder.ReadOnlyData(".rodata.str1.1", "a\0"u8), type: SymbolType.Object);
        builder.Global("b", builder.ReadOnlyData(".rodata.cst8", new byte[8], alignment: 8), type: SymbolType.Object);
        var image = Load(Link(static o => o with { GarbageCollectSections = false }, builder.Build()));

        Assert.NotNull(image.FindSection(".rodata"));
        Assert.Null(image.FindSection(".rodata.str1.1"));
        Assert.Null(image.FindSection(".text.main"));

        // The more strictly aligned member is placed at an aligned address.
        Assert.Equal(0UL, image.Symbols["b"] % 8);
    }

    private static Version VersionOf(string requirement) => Version.Parse(requirement[(requirement.LastIndexOf('_') + 1)..]);

    private static byte[] ProgramWithAllKindsOfSections()
    {
        var builder = new ElfObjectBuilder();
        var text = builder.Text(".text", Code.Return(0));
        builder.Global("main", text);
        builder.Global("constructor", text);
        builder.ReadOnlyData(".rodata", "constant\0"u8);
        builder.Data(".data", [1, 2, 3, 4]);
        builder.NoBits(".bss", 64);
        var initArray = builder.AddSection(".init_array", new byte[8], SectionFlags.Alloc | SectionFlags.Write, SectionType.InitArray, 8);
        builder.Relocate(initArray, 0, RelocX64.Abs64, "constructor");

        // Reference an imported data symbol through the GOT so that a .got exists.
        var user = builder.Text(".text.user", 0x48, 0x8b, 0x05, 0x00, 0x00, 0x00, 0x00, 0xc3); // mov rax, [rip + stdout@GOT] ; ret
        builder.Relocate(user, 3, RelocX64.RexGotPcRelX, "stdout", -4);
        builder.Relocate(initArray, 0, RelocX64.None, user);
        return builder.Build();
    }
}
