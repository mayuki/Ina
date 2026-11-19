using Ina.Linker.Elf;
using Ina.Linker.MachO;
using Ina.Linker.Tests.Support;

namespace Ina.Linker.Tests;

/// <summary>
/// The unwind tables: <c>__unwind_info</c>, which the unwinder searches by address, and <c>__eh_frame</c> for
/// the functions it defers to DWARF. Exceptions, stack traces and garbage collection of .NET code depend on
/// them, so the tables are checked by decoding them and by letting the system unwinder walk a real stack.
/// </summary>
public class MachOUnwindTests : MachOLinkerTestBase
{
    /// <summary>A frame whose caller is found through the frame pointer (UNWIND_ARM64_MODE_FRAME).</summary>
    private const uint FrameEncoding = 0x04000000;

    private const uint DwarfEncoding = CompactUnwind.ModeDwarfArm64;

    // The functions of the test program and their offsets in its text section.
    private const uint Callback = 0;
    private const uint Leaf = 20;
    private const uint Middle = 60;
    private const uint Main = 84;
    private const uint MiddleLength = 24;

    private enum MiddleFrame
    {
        CompactEntry,
        DwarfWithCompactEntry,
        DwarfOnly,
        Undescribed,
    }

    [Fact]
    public void UnwinderWalksFramesDescribedByCompactEntries()
    {
        var output = Link(BacktraceProgram(MiddleFrame.CompactEntry));

        // leaf, middle and main; the frames of the loader above main may or may not be described.
        Assert.InRange(Run(output).ExitCode, 3, 8);
    }

    [Fact]
    public void UnwinderWalksFramesDescribedByDwarf()
    {
        var output = Link(BacktraceProgram(MiddleFrame.DwarfWithCompactEntry));

        Assert.InRange(Run(output).ExitCode, 3, 8);
    }

    [Fact]
    public void FunctionThatOnlyHasAnFdeIsDescribedThroughIt()
    {
        var output = Link(BacktraceProgram(MiddleFrame.DwarfOnly));

        var image = Load(output);
        var table = image.ReadUnwindTable();
        Assert.Equal(DwarfEncoding, table.EncodingAt(image.Symbols["_middle"]) & CompactUnwind.ModeMask);
        Assert.InRange(Run(output).ExitCode, 3, 8);
    }

    [Fact]
    public void WalkStopsAtAFunctionWithoutUnwindInformation()
    {
        var output = Link(BacktraceProgram(MiddleFrame.Undescribed));

        // The function gets an explicit "nothing known" entry; otherwise the unwinder would apply the rules of the
        // function in front of it. The walk ends when it reaches a frame it cannot describe, so only the frame of
        // leaf is reported.
        var image = Load(output);
        Assert.Equal(0u, image.ReadUnwindTable().EncodingAt(image.Symbols["_middle"] + 8));
        Assert.Equal(1, Run(output).ExitCode);
    }

    [Fact]
    public void TableListsTheFunctionsInAddressOrderWithTheirEncodings()
    {
        var image = Load(Link(BacktraceProgram(MiddleFrame.CompactEntry)));

        // The callback has no unwind information. _leaf, _middle and _main share an encoding and have no data of
        // their own, so one entry covers all three.
        var table = image.ReadUnwindTable();
        Assert.Equal([(image.Symbols["_callback"], 0u), (image.Symbols["_leaf"], FrameEncoding)], table.Entries);
        Assert.Equal(FrameEncoding, table.EncodingAt(image.Symbols["_middle"]));
        Assert.Equal(FrameEncoding, table.EncodingAt(image.Symbols["_main"] + 16));
        Assert.Empty(table.Lsdas);
        Assert.Empty(table.PersonalitySlots);
        Assert.Null(image.FindSection("__TEXT", "__eh_frame"));
    }

    [Fact]
    public void DwarfEncodingNamesTheFdeAndTheFdeNamesTheFunction()
    {
        var image = Load(Link(BacktraceProgram(MiddleFrame.DwarfWithCompactEntry)));

        var middle = image.Symbols["_middle"];
        var encoding = image.ReadUnwindTable().EncodingAt(middle);
        Assert.Equal(DwarfEncoding, encoding & CompactUnwind.ModeMask);

        // The low bits are the offset of the FDE, whose first field after the header is the distance to the code.
        var frames = image.GetSection("__TEXT", "__eh_frame");
        var fde = frames.Address + (encoding & CompactUnwind.DwarfSectionOffsetMask);
        var cie = fde + 4 - image.U32(fde + 4);
        Assert.Equal(frames.Address, cie);
        Assert.Equal(0u, image.U32(cie + 4));
        Assert.Equal(middle, fde + 8 + image.U64(fde + 8));
        Assert.Equal((ulong)MiddleLength, image.U64(fde + 16));
        Assert.Equal(24UL + 40UL, frames.Size);
    }

    [Fact]
    public void RedundantAndOrphanedFdesAreDropped()
    {
        var builder = new MachObjectBuilder();
        var text = builder.Text(Code.Arm64(
            0x52800540, Ret,   // main:   mov w0, #42 ; ret
            Ret, Ret, Ret,     // framed: has a compact encoding and an FDE
            Ret, Ret, Ret));   // dead:   has an FDE, but nothing calls it
        builder.Global("_main", text);
        builder.Global("_framed", text, value: 8, desc: MachSymbolType.NoDeadStrip);
        builder.Global("_dead", text, value: 20);
        builder.UnwindEntry(text, 8, 12, FrameEncoding);
        var frames = builder.AddSection("__TEXT", "__eh_frame", [.. Cie(), .. Fde(24, 12), .. Fde(64, 12)], MachSectionType.Coalesced, 3);
        builder.Local("EH_frame0", frames);
        builder.Difference(frames, 24 + 8, "_framed", "EH_frame0", length: 3);
        builder.Difference(frames, 64 + 8, "_dead", "EH_frame0", length: 3);

        var image = Load(Link(builder.Build()));

        // The unwinder uses the compact encoding of the one and never asks about the other.
        Assert.Null(image.FindSection("__TEXT", "__eh_frame"));
        Assert.Equal(FrameEncoding, image.ReadUnwindTable().EncodingAt(image.Symbols["_framed"]));
    }

    [Fact]
    public void LanguageSpecificDataAndPersonalityAreRecordedAndKept()
    {
        var builder = new MachObjectBuilder();
        var text = builder.Text(Code.Arm64(0x52800540, Ret));
        builder.Global("_main", text);
        var tables = builder.AddSection("__TEXT", "__gcc_except_tab", [1, 2, 3, 4, 5, 6, 7, 8], alignmentLog2: 2);
        builder.Local("GCC_except_table0", tables);
        builder.Local("GCC_except_table1", tables, value: 4);
        builder.UnwindEntry(text, 0, 8, FrameEncoding | CompactUnwind.HasLsda, "___objc_personality_v0", tables, 4);

        var image = Load(Link(builder.Build()));

        // Nothing but the unwind entry refers to the second table, and nothing at all to the first.
        var main = image.Symbols["_main"];
        var table = image.ReadUnwindTable();
        Assert.Equal([5, 6, 7, 8], image.ReadSection(image.GetSection("__TEXT", "__gcc_except_tab")).ToArray());
        Assert.Equal(image.Symbols["GCC_except_table1"], table.Lsdas[main]);

        // The personality is the first (and only) one of the image, referred to through its GOT entry.
        Assert.Equal(FrameEncoding | CompactUnwind.HasLsda | (1u << CompactUnwind.PersonalityShift), table.EncodingAt(main));
        var slot = Assert.Single(table.PersonalitySlots);
        Assert.Contains(image.Binds, bind => bind.Address == slot && bind.SymbolName == "___objc_personality_v0");
    }

    [Fact]
    public void TableEndsBehindTheLastFunction()
    {
        var image = Load(Link(BacktraceProgram(MiddleFrame.CompactEntry)));

        var text = image.GetSection("__TEXT", "__text");
        Assert.Equal(text.Address + text.Size, image.ReadUnwindTable().End);
    }

    /// <summary>
    /// A program whose <c>main</c> calls <c>middle</c>, which calls <c>leaf</c>, which asks the system unwinder to
    /// walk the stack and returns the number of frames it visited.
    /// </summary>
    private static byte[] BacktraceProgram(MiddleFrame middle)
    {
        var builder = new MachObjectBuilder();
        var text = builder.Text(Code.Arm64(
            0xb9400028,  // callback: ldr  w8, [x1]
            0x11000508,  //           add  w8, w8, #1
            0xb9000028,  //           str  w8, [x1]
            0x52800000,  //           mov  w0, #0             ; _URC_NO_REASON: continue
            Ret,
            0xa9be7bfd,  // leaf:     stp  x29, x30, [sp, #-32]!
            0x910003fd,  //           mov  x29, sp
            0xb90013ff,  //           str  wzr, [sp, #16]
            0x90000000,  //           adrp x0, _callback@PAGE
            0x91000000,  //           add  x0, x0, _callback@PAGEOFF
            0x910043e1,  //           add  x1, sp, #16
            0x94000000,  //           bl   __Unwind_Backtrace
            0xb94013e0,  //           ldr  w0, [sp, #16]
            0xa8c27bfd,  //           ldp  x29, x30, [sp], #32
            Ret,
            StpFrame,    // middle:   stp  x29, x30, [sp, #-16]!
            0x910003fd,  //           mov  x29, sp
            0x94000000,  //           bl   _leaf
            0x11000000,  //           add  w0, w0, #0         ; keeps the call from being the last instruction
            LdpFrame,
            Ret,
            StpFrame,    // main:     stp  x29, x30, [sp, #-16]!
            0x910003fd,  //           mov  x29, sp
            0x94000000,  //           bl   _middle
            LdpFrame,
            Ret));
        builder.Global("_callback", text, Callback);
        builder.Global("_leaf", text, Leaf);
        builder.Global("_middle", text, Middle);
        builder.Global("_main", text, Main);
        builder.Arm64(text, Leaf + 12, MachRelocArm64.Page21, "_callback");
        builder.Arm64(text, Leaf + 16, MachRelocArm64.PageOff12, "_callback");
        builder.Arm64(text, Leaf + 24, MachRelocArm64.Branch26, "__Unwind_Backtrace");
        builder.Arm64(text, Middle + 8, MachRelocArm64.Branch26, "_leaf");
        builder.Arm64(text, Main + 8, MachRelocArm64.Branch26, "_middle");

        builder.UnwindEntry(text, Leaf, 40, FrameEncoding);
        builder.UnwindEntry(text, Main, 20, FrameEncoding);
        switch (middle)
        {
            case MiddleFrame.CompactEntry:
                builder.UnwindEntry(text, Middle, MiddleLength, FrameEncoding);
                break;
            case MiddleFrame.DwarfWithCompactEntry:
                builder.UnwindEntry(text, Middle, MiddleLength, DwarfEncoding);
                break;
        }

        if (middle is MiddleFrame.DwarfWithCompactEntry or MiddleFrame.DwarfOnly)
        {
            var frames = builder.AddSection("__TEXT", "__eh_frame", [.. Cie(), .. Fde(24, MiddleLength)], MachSectionType.Coalesced, 3);
            builder.Local("EH_frame0", frames);
            builder.Difference(frames, 24 + 8, "_middle", "EH_frame0", length: 3);
        }

        return builder.Build();
    }

    /// <summary>A CIE as compilers emit it for arm64: on entry the frame address is the stack pointer.</summary>
    private static byte[] Cie() =>
    [
        0x14, 0x00, 0x00, 0x00,  // length
        0x00, 0x00, 0x00, 0x00,  // this is a CIE
        0x01,                    // version
        0x7a, 0x52, 0x00,        // augmentation "zR"
        0x01,                    // code alignment factor
        0x78,                    // data alignment factor: -8
        0x1e,                    // return address register: x30
        0x01,                    // length of the augmentation data
        0x10,                    // R: addresses are 64-bit and relative to the field
        0x0c, 0x1f, 0x00,        // DW_CFA_def_cfa sp, 0
        0x00, 0x00, 0x00, 0x00,  // padding
    ];

    /// <summary>
    /// An FDE for a function that starts with <c>stp x29, x30, [sp, #-16]! ; mov x29, sp</c>. The address of the
    /// function is left to a relocation; the field holds the negated offset of itself, which turns the difference
    /// to the start of the section into a distance from the field.
    /// </summary>
    private static byte[] Fde(int offset, uint functionLength)
    {
        byte[] fde =
        [
            0x24, 0x00, 0x00, 0x00,                          // length
            0x00, 0x00, 0x00, 0x00,                          // distance to the CIE
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,  // start of the function
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,  // length of the function
            0x00,                                            // length of the augmentation data
            0x44,                                            // DW_CFA_advance_loc 4
            0x0e, 0x10,                                      // DW_CFA_def_cfa_offset 16
            0x9e, 0x01,                                      // DW_CFA_offset x30, -8
            0x9d, 0x02,                                      // DW_CFA_offset x29, -16
            0x44,                                            // DW_CFA_advance_loc 4
            0x0d, 0x1d,                                      // DW_CFA_def_cfa_register x29
            0x00, 0x00, 0x00, 0x00, 0x00,                    // padding
        ];
        LittleEndian.U32(fde, 4, (uint)(offset + 4));
        LittleEndian.I64(fde, 8, -(offset + 8));
        LittleEndian.U64(fde, 16, functionLength);
        return fde;
    }
}
