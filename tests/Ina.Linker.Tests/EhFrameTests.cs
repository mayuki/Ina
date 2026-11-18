using Ina.Linker.Elf;
using Ina.Linker.Tests.Support;

namespace Ina.Linker.Tests;

/// <summary>
/// The .NET runtime unwinds managed frames through <c>.eh_frame_hdr</c>, so the table must list exactly the
/// functions that made it into the image, sorted by address.
/// </summary>
public class EhFrameTests : LinkerTestBase
{
    // A CIE ("zR", pc-relative pointers) as emitted by clang for x86-64.
    private static readonly byte[] Cie =
    [
        0x14, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x7a, 0x52, 0x00, 0x01, 0x78, 0x10, 0x01,
        0x1b, 0x0c, 0x07, 0x08, 0x90, 0x01, 0x00, 0x00,
    ];

    [Fact]
    public void LookupTableListsLiveFunctionsSortedByAddress()
    {
        // "zeta" comes first in the input but "main" is placed first in the image: the table must be sorted.
        var (first, _) = ObjectWithUnwindInfo(("zeta", ".text.zeta", Code.Return(1)), ("dead", ".text.dead", Code.Return(2)));
        var (second, sections) = ObjectWithUnwindInfo(("main", ".text", Code.CallAndReturn));
        second.Relocate(sections[0], Code.CallOperand, RelocX64.Plt32, "zeta", -4);

        var output = Link(first.Build(), second.Build());
        var image = Load(output);
        var table = ReadLookupTable(image);

        Assert.Equal(2, table.Count);
        Assert.Equal([.. table.Select(static e => e.Start).Order()], table.Select(static e => e.Start));
        Assert.Equal(new[] { image.Symbols["main"], image.Symbols["zeta"] }.Order(), table.Select(static e => e.Start));
        Assert.Equal(1, Run(output).ExitCode);
    }

    [Fact]
    public void EveryFrameDescriptionPointsToItsFunctionAndItsCommonInformationEntry()
    {
        var (builder, _) = ObjectWithUnwindInfo(("main", ".text", Code.Return(0)), ("other", ".text.other", Code.Return(1)));
        var image = Load(Link(static o => o with { GarbageCollectSections = false }, builder.Build()));
        var frames = image.GetSection(".eh_frame");

        foreach (var (start, fde) in ReadLookupTable(image))
        {
            var record = image.ReadAt(fde, 16);
            var cie = fde + 4 - LittleEndian.U32(record, 4);

            // The CIE pointer leads back to a record with id 0 inside .eh_frame.
            Assert.InRange(cie, frames.Address, frames.Address + frames.Size - 1);
            Assert.Equal(0u, LittleEndian.U32(image.ReadAt(cie, 8), 4));

            // The pc-relative start address in the FDE matches the table entry.
            Assert.Equal(start, fde + 8 + (ulong)LittleEndian.I32(record, 8));
        }

        // The section ends with a zero length terminator.
        Assert.Equal(0u, LittleEndian.U32(image.ReadAt(frames.Address + frames.Size - 4, 4), 0));
    }

    [Fact]
    public void ImageWithoutUnwindInfoHasNoLookupTable()
    {
        var image = Load(Link(Code.Function("main", Code.Return(0))));

        Assert.Null(image.FindSection(".eh_frame_hdr"));
        Assert.DoesNotContain(image.Segments, static s => s.Type == SegmentType.GnuEhFrame);
    }

    /// <summary>Builds an object in which every function has its own section and an FDE describing it.</summary>
    private static (ElfObjectBuilder Builder, List<ElfObjectBuilder.Section> Code) ObjectWithUnwindInfo(params (string Name, string Section, byte[] Code)[] functions)
    {
        var builder = new ElfObjectBuilder();
        var frames = new List<byte>(Cie);
        var sections = new List<ElfObjectBuilder.Section>();
        foreach (var (name, sectionName, code) in functions)
        {
            var section = builder.Text(sectionName, code);
            builder.Global(name, section);
            sections.Add(section);

            // FDE: length, CIE pointer, pc begin (relocated), pc range, augmentation length, padding.
            var offset = frames.Count;
            frames.AddRange([0x10, 0x00, 0x00, 0x00]);
            frames.AddRange(BitConverter.GetBytes((uint)(offset + 4)));
            frames.AddRange([0x00, 0x00, 0x00, 0x00]);
            frames.AddRange(BitConverter.GetBytes((uint)code.Length));
            frames.AddRange([0x00, 0x00, 0x00, 0x00]);
        }

        var ehFrame = builder.AddSection(".eh_frame", frames.ToArray(), SectionFlags.Alloc, SectionType.X86_64Unwind, 8);
        for (var i = 0; i < sections.Count; i++)
        {
            builder.Relocate(ehFrame, (ulong)(Cie.Length + i * 20 + 8), RelocX64.Pc32, sections[i]);
        }

        return (builder, sections);
    }

    private static List<(ulong Start, ulong Fde)> ReadLookupTable(ElfImage image)
    {
        var header = image.GetSection(".eh_frame_hdr");
        var data = image.SectionData(header);
        Assert.Equal(1, data[0]);

        // The header starts with a pc-relative pointer to .eh_frame.
        Assert.Equal(image.GetSection(".eh_frame").Address, header.Address + 4 + (ulong)LittleEndian.I32(data, 4));
        Assert.Contains(image.Segments, s => s.Type == SegmentType.GnuEhFrame && s.Address == header.Address);

        var entries = new List<(ulong, ulong)>();
        var count = (int)LittleEndian.U32(data, 8);
        for (var i = 0; i < count; i++)
        {
            entries.Add((header.Address + (ulong)LittleEndian.I32(data, 12 + i * 8), header.Address + (ulong)LittleEndian.I32(data, 16 + i * 8)));
        }

        return entries;
    }
}
