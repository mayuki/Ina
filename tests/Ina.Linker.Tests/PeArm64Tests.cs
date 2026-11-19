using Ina.Linker.Elf;
using Ina.Linker.Pe;
using Ina.Linker.Tests.Support;

namespace Ina.Linker.Tests;

/// <summary>
/// Windows executables for ARM64. They are checked by decoding what the linker wrote, which needs no ARM64
/// machine: the instructions that relocations patched, the thunks, and the tables of the image.
/// </summary>
public class PeArm64Tests : PeLinkerTestBase
{
    private const uint Ret = 0xd65f03c0;

    [Fact]
    public void HeadersDescribeAProgramForArm64()
    {
        var image = Load(LinkArm64(MainReturning(0)));

        Assert.Equal(0xaa64, image.Machine);
        Assert.Equal(3, image.Subsystem);
        Assert.Equal(0x8160, image.DllCharacteristics);
        Assert.Equal(".text", image.SectionOf(image.EntryPoint).Name);

        // The startup code is the same as for x64, so it needs the same functions of the C runtime.
        var runtime = image.Imports.Single(static i => i.Library == "api-ms-win-crt-runtime-l1-1-0.dll").Names;
        Assert.Equal(["__p___argc", "__p___wargv", "_configure_wide_argv", "_initterm", "_initterm_e", "_set_app_type", "exit"], runtime);
    }

    [Fact]
    public void CallOfAnImportedFunctionGoesThroughAThunkThatLoadsTheAddressSlot()
    {
        var builder = new CoffObjectBuilder(CoffMachine.Arm64);
        var text = builder.Text(".text$mn", Code.Arm64(
            0x94000000, // bl  puts
            Ret));
        builder.External("wmain", text);
        builder.External("probe", text);
        builder.Relocate(text, 0, RelArm64.Branch26, "puts");

        var image = Load(Link(static o => o with { Architecture = TargetArchitecture.Arm64, ExportedSymbols = ["probe"] }, builder.Build()));

        var call = image.Exports["probe"];
        var thunk = BranchTarget(call, image.U32(call));

        // adrp x16, slot ; ldr x16, [x16, #:lo12:slot] ; br x16
        var adrp = image.U32(thunk);
        var ldr = image.U32(thunk + 4);
        Assert.Equal(0x90000010u, adrp & 0x9f00001f);
        Assert.Equal(0xf9400210u, ldr & 0xffc003ff);
        Assert.Equal(0xd61f0200u, image.U32(thunk + 8));
        var slot = PageOf(thunk, adrp) + ((ldr >> 10) & 0xfff) * 8;
        Assert.Equal("puts", image.ImportSlots[slot]);
    }

    [Fact]
    public void PageRelativeAddressesAreSplitBetweenTheInstructionsOfAPair()
    {
        // adrp materializes the 4 KiB page of an address; the instruction that follows adds the offset inside it.
        var builder = new CoffObjectBuilder(CoffMachine.Arm64);
        var text = builder.Text(".text$mn", Code.Arm64(
            0x90000000, // adrp x0, variable
            0x91000000, // add  x0, x0, :lo12:variable
            0x90000001, // adrp x1, variable + 8
            0xf9400021, // ldr  x1, [x1, :lo12:variable + 8]
            0x90000002, // adrp x2, variable
            0xb9400042, // ldr  w2, [x2, :lo12:variable]
            Ret));
        builder.External("wmain", text);
        builder.External("probe", text);
        builder.External("variable", builder.Data(".data", new byte[0x2000], 16), 0x1238);
        builder.Relocate(text, 0, RelArm64.PageBaseRel21, "variable");
        builder.Relocate(text, 4, RelArm64.PageOffset12A, "variable");
        builder.Relocate(text, 8, RelArm64.PageBaseRel21, "variable");
        builder.Relocate(text, 12, RelArm64.PageOffset12L, "variable");
        builder.Relocate(text, 16, RelArm64.PageBaseRel21, "variable");
        builder.Relocate(text, 20, RelArm64.PageOffset12L, "variable");

        // The addend of the second pair is part of the instructions: 8 bytes, and one unit of the 8-byte load.
        var code = builder.Build();
        var patched = Patch(code, 8, 0x90000001 | 8u >> 2 << 5, 12, 0xf9400021 | 1u << 10);

        var image = Load(Link(static o => o with { Architecture = TargetArchitecture.Arm64, ExportedSymbols = ["probe", "variable"] }, patched));

        var probe = image.Exports["probe"];
        var variable = image.Exports["variable"];
        Assert.Equal(variable, PageOf(probe, image.U32(probe)) + ((image.U32(probe + 4) >> 10) & 0xfff));
        Assert.Equal(variable + 8, PageOf(probe + 8, image.U32(probe + 8)) + ((image.U32(probe + 12) >> 10) & 0xfff) * 8);
        Assert.Equal(variable, PageOf(probe + 16, image.U32(probe + 16)) + ((image.U32(probe + 20) >> 10) & 0xfff) * 4);
    }

    [Fact]
    public void MisalignedLoadIsRejected()
    {
        // The offset of a 64-bit load is encoded in units of 8 bytes, so the target has to be aligned.
        var builder = new CoffObjectBuilder(CoffMachine.Arm64);
        builder.External("odd", builder.Data(".data", new byte[16], 16), 4);
        var text = builder.Text(".text$mn", Code.Arm64(0x90000001, 0xf9400021, Ret)); // adrp x1, odd ; ldr x1, [x1, :lo12:odd]
        builder.External("wmain", text);
        builder.Relocate(text, 0, RelArm64.PageBaseRel21, "odd");
        builder.Relocate(text, 4, RelArm64.PageOffset12L, "odd");

        var error = Assert.Throws<LinkerException>(() => LinkArm64(builder.Build()));

        Assert.Contains("'odd' is not aligned", error.Message);
    }

    [Fact]
    public void AbsoluteAddressesAreListedForTheLoader()
    {
        var builder = new CoffObjectBuilder(CoffMachine.Arm64);
        builder.External("wmain", builder.Text(".text$mn", Code.Arm64(Ret)));
        var text = builder.ReadOnlyData(".rdata", "text\0"u8);
        var pointer = builder.Data(".data", new byte[8]);
        builder.External("pointer", pointer);
        builder.Relocate(pointer, 0, RelArm64.Addr64, text);

        var image = Load(Link(static o => o with { Architecture = TargetArchitecture.Arm64, ExportedSymbols = ["pointer"] }, builder.Build()));

        var slot = image.Exports["pointer"];
        Assert.Contains(slot, image.BaseRelocations);
        Assert.Equal("text", image.CString((uint)(image.U64(slot) - image.ImageBase)));
    }

    [Fact]
    public void ThreadLocalOffsetsAreSplitIntoAHighAndALowPart()
    {
        // The offset of a thread-local variable in the TLS block is added in two steps of 12 bits each.
        var builder = new CoffObjectBuilder(CoffMachine.Arm64);
        const uint Template = CoffSectionFlags.InitializedData | CoffSectionFlags.Read | CoffSectionFlags.Write;
        var block = builder.AddSection(".tls$", new byte[0x3000], Template, 16);
        builder.External("thread_variable", block, 0x2468);
        var text = builder.Text(".text$mn", Code.Arm64(
            0x90000008, // adrp x8, _tls_index
            0xb9400108, // ldr  w8, [x8, :lo12:_tls_index]
            0xf9402e49, // ldr  x9, [x18, #0x58]
            0xf8687929, // ldr  x9, [x9, x8, lsl #3]
            0x91400129, // add  x9, x9, #:secrel_hi12:thread_variable, lsl #12
            0x91000129, // add  x9, x9, #:secrel_lo12:thread_variable
            0x91400120, // add  x0, x9, #:secrel_hi12:thread_variable, lsl #12
            0xb9400000, // ldr  w0, [x0, #:secrel_lo12:thread_variable]
            Ret));
        builder.External("wmain", text);
        builder.External("probe", text);
        builder.Relocate(text, 0, RelArm64.PageBaseRel21, "_tls_index");
        builder.Relocate(text, 4, RelArm64.PageOffset12L, "_tls_index");
        builder.Relocate(text, 16, RelArm64.SecRelHigh12A, "thread_variable");
        builder.Relocate(text, 20, RelArm64.SecRelLow12A, "thread_variable");
        builder.Relocate(text, 24, RelArm64.SecRelHigh12A, "thread_variable");
        builder.Relocate(text, 28, RelArm64.SecRelLow12L, "thread_variable");

        var image = Load(Link(static o => o with { Architecture = TargetArchitecture.Arm64, ExportedSymbols = ["probe", "thread_variable"] }, builder.Build()));

        // The offset counts from the start of the initialization image, which the TLS directory names.
        var (directory, _) = image.Directory(PeImage.DirectoryTls);
        var offset = image.Exports["thread_variable"] - (uint)(image.U64(directory) - image.ImageBase);
        var probe = image.Exports["probe"];
        Assert.True(offset > 0x2468);
        Assert.Equal(offset >> 12, (image.U32(probe + 16) >> 10) & 0xfff);
        Assert.Equal(offset & 0xfff, (image.U32(probe + 20) >> 10) & 0xfff);
        Assert.Equal(offset >> 12, (image.U32(probe + 24) >> 10) & 0xfff);
        Assert.Equal((offset & 0xfff) / 4, (image.U32(probe + 28) >> 10) & 0xfff);
    }

    [Fact]
    public void FunctionTableHasCompactEntriesSortedByAddress()
    {
        var image = Load(LinkArm64(MainReturning(0)));

        // Entries are eight bytes for ARM64: the start of the function and its unwind data. The generated startup
        // code and the thread-local initialization describe their frames in the packed form (low bits 01).
        var (rva, size) = image.Directory(PeImage.DirectoryException);
        Assert.Equal(2u * 8, size);
        Assert.True(image.U32(rva) < image.U32(rva + 8));
        Assert.All(new[] { rva, rva + 8 }, entry =>
        {
            var unwind = image.U32(entry + 4);
            Assert.Equal(1u, unwind & 3);                    // packed unwind data
            Assert.Equal(3u, (unwind >> 21) & 3);            // frame chain saved with a pair instruction
            Assert.Equal(2u, unwind >> 23);                  // 32 bytes of stack

            // The function length reaches exactly to the return instruction at its end.
            var length = ((unwind >> 2) & 0x7ff) * 4;
            Assert.Equal(Ret, image.U32(image.U32(entry) + length - 4));
            Assert.Equal(0xa9be7bfdu, image.U32(image.U32(entry))); // stp x29, x30, [sp, #-32]!
        });
        Assert.Contains(image.EntryPoint, new[] { image.U32(rva), image.U32(rva + 8) });
    }

    [Fact]
    public void RuntimeSupportOfTheCompilerIsLinkedWhenItIsCalled()
    {
        // What code compiled for ARM64 calls in the static C runtime: the stack protection of function prologs and
        // epilogs, the stack probe, and the interlocked operations, which are not expanded inline like on x64.
        string[] helpers =
        [
            "__security_push_cookie", "__security_pop_cookie", "__chkstk", "_InterlockedCompareExchange", "_InterlockedCompareExchange64",
            "_InterlockedCompareExchangePointer", "_InterlockedExchange", "_InterlockedExchangePointer", "_InterlockedIncrement",
            "_InterlockedDecrement", "_InterlockedExchangeAdd", "_InterlockedAnd", "_InterlockedOr", "_InterlockedXor64",
        ];
        var builder = new CoffObjectBuilder(CoffMachine.Arm64);
        var text = builder.Text(".text$mn", Code.Arm64([.. helpers.Select(static _ => 0x94000000u), Ret]));
        builder.External("wmain", text);
        builder.External("probe", text);
        for (var i = 0; i < helpers.Length; i++)
        {
            builder.Relocate(text, (uint)i * 4, RelArm64.Branch26, helpers[i]);
        }

        var image = Load(Link(static o => o with { Architecture = TargetArchitecture.Arm64, ExportedSymbols = ["probe"] }, builder.Build()));

        // Every call lands on code inside the image, not on an import, and each helper ends in a return or a trap.
        var probe = image.Exports["probe"];
        var targets = Enumerable.Range(0, helpers.Length).Select(i => BranchTarget(probe + (uint)i * 4, image.U32(probe + (uint)i * 4))).ToList();
        Assert.Equal(helpers.Length, targets.Distinct().Count());
        Assert.All(targets, target => Assert.Equal(".text", image.SectionOf(target).Name));
        Assert.DoesNotContain(image.Imports.SelectMany(static i => i.Names), name => helpers.Contains(name));

        // The stack protection brings the cookie and the load configuration that lets Windows randomize it.
        Assert.NotEqual((0u, 0u), image.Directory(PeImage.DirectoryLoadConfig));
    }

    [Fact]
    public void BranchThatDoesNotReachItsTargetIsReported()
    {
        // A conditional branch reaches one megabyte; the function it refers to is placed further away.
        var builder = new CoffObjectBuilder(CoffMachine.Arm64);
        var text = builder.Text(".text$mn", Code.Arm64(0x54000000, Ret)); // b.eq far
        builder.External("wmain", text);
        builder.Relocate(text, 0, RelArm64.Branch19, "far");
        builder.Text(".text$mn", new byte[0x20_0000]);
        builder.External("far", builder.Text(".text$mn", Code.Arm64(Ret)));

        var error = Assert.Throws<LinkerException>(() => LinkArm64(builder.Build()));

        Assert.Contains("IMAGE_REL_ARM64_BRANCH19 against 'far' is out of range", error.Message);
    }

    private static byte[] MainReturning(int value)
    {
        var builder = new CoffObjectBuilder(CoffMachine.Arm64);
        builder.External("wmain", builder.Text(".text$mn", Code.Arm64(0x52800000 | (uint)value << 5, Ret))); // mov w0, #value ; ret
        return builder.Build();
    }

    /// <summary>The address that a <c>b</c> or <c>bl</c> instruction at <paramref name="address"/> jumps to.</summary>
    private static uint BranchTarget(uint address, uint instruction)
    {
        Assert.Equal(0x14000000u, instruction & 0x7c000000);
        return (uint)(address + ((int)(instruction << 6) >> 4));
    }

    /// <summary>The page that an <c>adrp</c> instruction at <paramref name="address"/> computes.</summary>
    private static uint PageOf(uint address, uint instruction)
    {
        Assert.Equal(0x90000000u, instruction & 0x9f000000);
        var pages = (int)((((instruction >> 5) & 0x7ffff) << 2 | ((instruction >> 29) & 3)) << 11) >> 11;
        return (uint)((address & ~0xfffu) + ((long)pages << 12));
    }

    /// <summary>Replaces instructions of the first section of an object, whose data follows the headers directly.</summary>
    private static byte[] Patch(byte[] coffObject, params uint[] offsetsAndInstructions)
    {
        var patched = (byte[])coffObject.Clone();
        var sectionCount = LittleEndian.U16(patched, 2);
        var textOffset = (int)LittleEndian.U32(patched, 20 + 20);
        Assert.True(textOffset >= 20 + sectionCount * 40);
        for (var i = 0; i < offsetsAndInstructions.Length; i += 2)
        {
            LittleEndian.U32(patched, textOffset + (int)offsetsAndInstructions[i], offsetsAndInstructions[i + 1]);
        }

        return patched;
    }
}
