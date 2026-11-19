using Ina.Linker.Elf;
using Ina.Linker.MachO;
using Ina.Linker.Tests.Support;

namespace Ina.Linker.Tests;

/// <summary>
/// x86-64 support (Intel Macs). The encoding of every relocation is checked by decoding the patched instructions,
/// which works on any host; in addition the programs are executed on an Intel Mac or under Rosetta.
/// </summary>
public class MachOX64Tests : MachOLinkerTestBase
{
    /// <summary>A frame whose caller is found through the frame pointer (UNWIND_X86_64_MODE_RBP_FRAME).</summary>
    private const uint FrameEncoding = 0x01000000;

    [Fact]
    public void ImageTargetsIntelMacsWithTheirPageSize()
    {
        var output = LinkX64(X64Function("_main", 0xb8, 0x2a, 0x00, 0x00, 0x00, 0xc3)); // mov eax, 42 ; ret
        var image = Load(output);

        Assert.Equal(MachCpu.X64, image.CpuType);
        Assert.Equal(MachCpu.SubtypeX64All, image.CpuSubtype & 0xffffff);
        Assert.Equal("/usr/lib/libSystem.B.dylib", Assert.Single(image.Libraries).Name);
        Assert.True(image.Signature!.HashesMatch);

        // Intel Macs use 4 KiB pages.
        Assert.All(image.Segments.Skip(1), static segment => Assert.Equal(0UL, segment.Address % 0x1000));
        Assert.Equal(0x1000UL, image.GetSegment("__TEXT").VmSize);
        Assert.Equal(42, RunX64(output).ExitCode);
    }

    [Fact]
    public void CallToImportedFunctionGoesThroughAStubThatJumpsThroughItsGotEntry()
    {
        var output = LinkX64(PrintMessageProgram(sectionRelative: false));
        var image = Load(output);
        var main = image.Symbols["_main"];

        // lea rdi, [rip + message]
        Assert.Equal("hello from ina\0"u8, image.ReadAt(RipTarget(image, main + 4), 15));

        // call puts -> jmp [rip + slot]
        var stubs = image.GetSection("__TEXT", "__stubs");
        var stub = RipTarget(image, main + 9);
        Assert.Equal(stubs.Address, stub);
        Assert.Equal(6u, stubs.Reserved2);
        Assert.Equal([0xff, 0x25], image.ReadAt(stub, 2).ToArray());
        var slot = RipTarget(image, stub + 2);
        Assert.Equal(image.GetSection("__DATA_CONST", "__got").Address, slot);
        var bind = Assert.Single(image.Binds);
        Assert.Equal((slot, "_puts"), (bind.Address, bind.SymbolName));
        Assert.Equal((0, "hello from ina\n"), RunX64(output));
    }

    [Fact]
    public void ReferenceToASectionWithoutSymbolIsResolvedByAddress()
    {
        // Compilers refer to string literals this way on x86-64: the displacement holds the distance inside the
        // object file and the relocation names the section.
        var output = LinkX64(PrintMessageProgram(sectionRelative: true));
        var image = Load(output);

        Assert.Equal("hello from ina\0"u8, image.ReadAt(RipTarget(image, image.Symbols["_main"] + 4), 15));
        Assert.Equal((0, "hello from ina\n"), RunX64(output));
    }

    [Fact]
    public void DisplacementInFrontOfAnImmediateCountsFromTheEndOfTheInstruction()
    {
        var builder = new MachObjectBuilder(TargetArchitecture.X64);
        var counter = builder.ZeroFill("__bss", 8);
        builder.Local("_counter", counter);
        var text = builder.Text(
            0xc7, 0x05, 0xfc, 0xff, 0xff, 0xff, 0x07, 0x00, 0x00, 0x00, // mov dword [rip + _counter], 7   ; the field holds -4
            0xc7, 0x05, 0x00, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00, // mov dword [rip + counter + 4], 5 ; section-relative
            0x8b, 0x05, 0x00, 0x00, 0x00, 0x00,                         // mov eax, [rip + _counter]
            0x03, 0x05, 0x04, 0x00, 0x00, 0x00,                         // add eax, [rip + _counter + 4]    ; the field holds 4
            0xc3);                                                      // ret
        builder.Global("_main", text);
        builder.X64(text, 2, MachRelocX64.Signed4, "_counter");
        builder.X64SectionRelative(text, 12, counter, 4, trailing: 4);
        builder.X64(text, 22, MachRelocX64.Signed, "_counter");
        builder.X64(text, 28, MachRelocX64.Signed, "_counter");
        var output = LinkX64(builder.Build());

        var image = Load(output);
        var main = image.Symbols["_main"];
        var variable = image.Symbols["_counter"];
        Assert.Equal(variable, RipTarget(image, main + 2, trailing: 4));
        Assert.Equal(variable + 4, RipTarget(image, main + 12, trailing: 4));
        Assert.Equal(variable, RipTarget(image, main + 22));
        Assert.Equal(variable + 4, RipTarget(image, main + 28));
        Assert.Equal(12, RunX64(output).ExitCode);
    }

    [Fact]
    public void GotLoadIsRewrittenForSymbolsOfTheImageAndKeptForImports()
    {
        var builder = new MachObjectBuilder(TargetArchitecture.X64);
        builder.Global("_value", builder.Data([0x11, 0, 0, 0]));
        builder.WeakReference("_not_available_anywhere");
        var text = builder.Text(
            0x48, 0x8b, 0x05, 0x00, 0x00, 0x00, 0x00,       // mov rax, [rip + _value@GOT]
            0x8b, 0x00,                                     // mov eax, [rax]
            0x48, 0x8b, 0x0d, 0x00, 0x00, 0x00, 0x00,       // mov rcx, [rip + ___stdoutp@GOT]
            0x48, 0x83, 0x3d, 0xff, 0xff, 0xff, 0xff, 0x00, // cmp qword [rip + _not_available_anywhere@GOT], 0
            0xc3);                                          // ret
        builder.Global("_main", text);
        builder.X64(text, 3, MachRelocX64.GotLoad, "_value");
        builder.X64(text, 12, MachRelocX64.GotLoad, "___stdoutp");
        builder.X64(text, 19, MachRelocX64.Got, "_not_available_anywhere");
        var output = LinkX64(builder.Build());

        var image = Load(output);
        var main = image.Symbols["_main"];
        var got = image.GetSection("__DATA_CONST", "__got");

        // The load of a local address became "lea rax, [rip + _value]".
        Assert.Equal(0x8d, image.ReadAt(main + 1, 1)[0]);
        Assert.Equal(image.Symbols["_value"], RipTarget(image, main + 3));

        // The import is loaded from its GOT entry. The comparison is not a plain load and cannot be rewritten, so
        // the missing weak symbol gets an entry that holds null.
        Assert.Equal(0x8b, image.ReadAt(main + 10, 1)[0]);
        var imported = RipTarget(image, main + 12);
        var missing = RipTarget(image, main + 19, trailing: 1);
        Assert.Equal(16UL, got.Size);
        Assert.Equal([imported, missing], [got.Address, got.Address + 8]);
        Assert.Equal((imported, "___stdoutp"), (Assert.Single(image.Binds).Address, image.Binds[0].SymbolName));
        Assert.Equal(0UL, image.U64(missing));
        Assert.Empty(image.Rebases);
        Assert.Equal(0x11, RunX64(output).ExitCode);
    }

    [Fact]
    public void ThreadLocalVariableIsReachedThroughItsDescriptor()
    {
        var builder = new MachObjectBuilder(TargetArchitecture.X64);
        var initialValue = builder.AddSection("__DATA", "__thread_data", [0x15, 0, 0, 0], MachSectionType.ThreadLocalRegular, 2);
        builder.Local("_value$tlv$init", initialValue);
        var descriptor = builder.AddSection("__DATA", "__thread_vars", new byte[24], MachSectionType.ThreadLocalVariables, 3);
        builder.Global("_value", descriptor);
        builder.Pointer(descriptor, 0, "__tlv_bootstrap");
        builder.Pointer(descriptor, 16, "_value$tlv$init");
        var text = builder.Text(
            0x55,                                     // push rbp
            0x48, 0x89, 0xe5,                         // mov  rbp, rsp
            0x48, 0x8b, 0x3d, 0x00, 0x00, 0x00, 0x00, // mov  rdi, [rip + _value@TLVP]
            0xff, 0x17,                               // call [rdi]
            0x8b, 0x00,                               // mov  eax, [rax]
            0x5d,                                     // pop  rbp
            0xc3);                                    // ret
        builder.Global("_main", text);
        builder.X64(text, 7, MachRelocX64.Tlv, "_value");
        var output = LinkX64(builder.Build());

        // The code computes the address of the descriptor ("lea") instead of loading it from a pointer.
        var image = Load(output);
        var main = image.Symbols["_main"];
        Assert.Equal(0x8d, image.ReadAt(main + 5, 1)[0]);
        Assert.Equal(image.Symbols["_value"], RipTarget(image, main + 7));
        Assert.NotEqual(0u, image.Flags & MachHeader.HasTlvDescriptors);
        Assert.Equal(0x15, RunX64(output).ExitCode);
    }

    [Fact]
    public void DifferencesWithALabelWithoutSymbolAreResolved()
    {
        // Relative tables: "entry - table" where the table has a symbol but the entry does not (a string), and
        // "target - entry address" where the place in the table has no symbol but the target does.
        var builder = new MachObjectBuilder(TargetArchitecture.X64);
        var strings = builder.CString("zero\0one\0"u8);
        var table = builder.Const(new byte[12], alignmentLog2: 2);
        builder.Local("_table", table);
        var text = builder.Text(
            0x8b, 0x05, 0x04, 0x00, 0x00, 0x00,       // main:  mov eax, [rip + _table + 4]
            0xc3,                                     //        ret
            0xc3);                                    // other: ret
        builder.Global("_main", text);
        builder.Global("_other", text, value: 7);
        builder.X64(text, 2, MachRelocX64.Signed, "_table");

        // table[1] = &"one" - table: the field holds the address of the string inside the object file.
        builder.Relocate(table, 4, MachRelocX64.Subtractor, "_table", pcRel: false);
        builder.Relocate(table, 4, MachRelocX64.Unsigned, strings, pcRel: false);
        table.AddressFields.Add((4, 4, strings, 5));

        // table[2] = _other - &table[2]: the field holds minus its own address inside the object file.
        builder.Relocate(table, 8, MachRelocX64.Subtractor, table, pcRel: false);
        builder.Relocate(table, 8, MachRelocX64.Unsigned, "_other", pcRel: false);
        table.NegatedAddressFields.Add((8, 4, table, 8));
        var output = LinkX64(builder.Build());

        var image = Load(output);
        var tableAddress = image.Symbols["_table"];
        var one = image.GetSection("__TEXT", "__cstring").Address;
        Assert.Equal("one\0"u8, image.ReadAt(one, 4));
        Assert.Equal((uint)(one - tableAddress), image.U32(tableAddress + 4));
        Assert.Equal((uint)(image.Symbols["_other"] - (tableAddress + 8)), image.U32(tableAddress + 8));
        Assert.Empty(image.Rebases);
    }

    [Fact]
    public void UnwindTablesWithoutRelocationsAreUnderstood()
    {
        var output = LinkX64(BacktraceProgram());
        var image = Load(output);

        // _middle is described by an FDE whose fields the assembler filled in without leaving relocations.
        var middle = image.Symbols["_middle"];
        var table = image.ReadUnwindTable();
        var encoding = table.EncodingAt(middle);
        Assert.Equal(CompactUnwind.ModeDwarfX64, encoding & CompactUnwind.ModeMask);
        Assert.Equal(FrameEncoding, table.EncodingAt(image.Symbols["_leaf"]));
        Assert.Equal(FrameEncoding, table.EncodingAt(image.Symbols["_main"]));

        var frames = image.GetSection("__TEXT", "__eh_frame");
        var fde = frames.Address + (encoding & CompactUnwind.DwarfSectionOffsetMask);
        Assert.Equal(middle, fde + 8 + image.U64(fde + 8));
        Assert.Equal(14UL, image.U64(fde + 16));

        // The data that only the FDE refers to is kept and found again.
        var lsda = fde + 25 + image.U64(fde + 25);
        Assert.Equal(image.Symbols["GCC_except_table1"], lsda);
        Assert.Equal([5, 6, 7, 8], image.ReadSection(image.GetSection("__TEXT", "__gcc_except_tab")).ToArray());

        // The CIE names the personality through its GOT entry.
        var cie = fde + 4 - image.U32(fde + 4);
        var slot = cie + 19 + (ulong)(long)(int)image.U32(cie + 19);
        Assert.Contains(image.Binds, bind => bind.Address == slot && bind.SymbolName == "___objc_personality_v0");
        Assert.InRange(RunX64(output).ExitCode, 3, 8);
    }

    [Fact]
    public void GeneratedFunctionsHaveTheSameJobAsOnArm64()
    {
        var builder = new MachObjectBuilder(TargetArchitecture.X64);
        builder.AddSection("__DATA", "__objc_imageinfo", new byte[8], MachSectionType.AttrNoDeadStrip, 2);
        var name = builder.CString("NSObject\0"u8);
        var text = builder.Text(
            0x55,                                     // push rbp
            0x48, 0x89, 0xe5,                         // mov  rbp, rsp
            0x53,                                     // push rbx
            0x50,                                     // push rax
            0x48, 0x8d, 0x3d, 0x00, 0x00, 0x00, 0x00, // lea  rdi, [rip + name]
            0xe8, 0x00, 0x00, 0x00, 0x00,             // call _objc_getClass
            0x48, 0x89, 0xc3,                         // mov  rbx, rax
            0x48, 0x89, 0xc7,                         // mov  rdi, rax
            0xe8, 0x00, 0x00, 0x00, 0x00,             // call _objc_msgSend$class
            0x31, 0xc9,                               // xor  ecx, ecx
            0x48, 0x39, 0xd8,                         // cmp  rax, rbx
            0x0f, 0x94, 0xc1,                         // sete cl
            0x89, 0xc8,                               // mov  eax, ecx
            0x48, 0x83, 0xc4, 0x08,                   // add  rsp, 8
            0x5b,                                     // pop  rbx
            0x5d,                                     // pop  rbp
            0xc3,                                     // ret
            0xe8, 0x00, 0x00, 0x00, 0x00,             // check: call ___isOSVersionAtLeast
            0xc3);                                    //        ret
        builder.Global("_main", text);
        builder.Global("_check", text, value: 46, desc: MachSymbolType.NoDeadStrip);
        builder.X64SectionRelative(text, 9, name);
        builder.X64(text, 14, MachRelocX64.Branch, "_objc_getClass");
        builder.X64(text, 25, MachRelocX64.Branch, "_objc_msgSend$class");
        builder.X64(text, 47, MachRelocX64.Branch, "___isOSVersionAtLeast");
        var output = LinkX64(builder.Build());

        // The selector stub: mov rsi, [rip + selector reference] ; jmp [rip + _objc_msgSend@GOT]
        var image = Load(output);
        var stub = RipTarget(image, image.Symbols["_main"] + 25);
        var selectors = image.GetSection("__DATA", "__objc_selrefs");
        Assert.Equal(image.GetSection("__TEXT", "__objc_stubs").Address, stub);
        Assert.Equal([0x48, 0x8b, 0x35], image.ReadAt(stub, 3).ToArray());
        Assert.Equal(selectors.Address, RipTarget(image, stub + 3));
        Assert.Equal([0xff, 0x25], image.ReadAt(stub + 7, 2).ToArray());
        var slot = RipTarget(image, stub + 9);
        Assert.Contains(image.Binds, bind => bind.Address == slot && bind.SymbolName == "_objc_msgSend");
        Assert.Equal("class\0"u8, image.ReadAt(image.U64(selectors.Address), 6));

        // The version check of the compiler support library ends up in the C library.
        var older = RipTarget(image, image.Symbols["_check"] + 1);
        Assert.Equal(image.Symbols["___isOSVersionAtLeast"], older);
        Assert.Equal(image.Symbols["___isPlatformVersionAtLeast"], RipTarget(image, older + 12));
        Assert.Contains("__availability_version_check", image.UndefinedSymbols);
        Assert.Equal(1, RunX64(output).ExitCode);
    }

    private static byte[] X64Function(string name, params byte[] code)
    {
        var builder = new MachObjectBuilder(TargetArchitecture.X64);
        builder.Global(name, builder.Text(code));
        return builder.Build();
    }

    private static byte[] PrintMessageProgram(bool sectionRelative)
    {
        var builder = new MachObjectBuilder(TargetArchitecture.X64);
        var message = builder.CString("hello from ina\0"u8);
        var text = builder.Text(
            0x55,                                     // push rbp
            0x48, 0x8d, 0x3d, 0x00, 0x00, 0x00, 0x00, // lea  rdi, [rip + message]
            0xe8, 0x00, 0x00, 0x00, 0x00,             // call _puts
            0x31, 0xc0,                               // xor  eax, eax
            0x5d,                                     // pop  rbp
            0xc3);                                    // ret
        builder.Global("_main", text);
        if (sectionRelative)
        {
            builder.X64SectionRelative(text, 4, message);
        }
        else
        {
            builder.Local("L_.str", message);
            builder.X64(text, 4, MachRelocX64.Signed, "L_.str");
        }

        builder.X64(text, 9, MachRelocX64.Branch, "_puts");
        return builder.Build();
    }

    /// <summary>
    /// The x86-64 version of the program in <see cref="MachOUnwindTests"/>: <c>main</c> calls <c>middle</c>, which
    /// calls <c>leaf</c>, which lets the system unwinder walk the stack and returns the number of frames.
    /// </summary>
    private static byte[] BacktraceProgram()
    {
        const uint Leaf = 5;
        const uint Middle = 45;
        const uint Main = 59;
        var builder = new MachObjectBuilder(TargetArchitecture.X64);
        var text = builder.Text(
            0xff, 0x06,                               // callback: inc  dword [rsi]
            0x31, 0xc0,                               //           xor  eax, eax         ; _URC_NO_REASON: continue
            0xc3,                                     //           ret
            0x55,                                     // leaf:     push rbp
            0x48, 0x89, 0xe5,                         //           mov  rbp, rsp
            0x48, 0x83, 0xec, 0x10,                   //           sub  rsp, 16
            0xc7, 0x45, 0xfc, 0x00, 0x00, 0x00, 0x00, //           mov  dword [rbp - 4], 0
            0x48, 0x8d, 0x3d, 0x00, 0x00, 0x00, 0x00, //           lea  rdi, [rip + _callback]
            0x48, 0x8d, 0x75, 0xfc,                   //           lea  rsi, [rbp - 4]
            0xe8, 0x00, 0x00, 0x00, 0x00,             //           call __Unwind_Backtrace
            0x8b, 0x45, 0xfc,                         //           mov  eax, [rbp - 4]
            0x48, 0x83, 0xc4, 0x10,                   //           add  rsp, 16
            0x5d,                                     //           pop  rbp
            0xc3,                                     //           ret
            0x55,                                     // middle:   push rbp
            0x48, 0x89, 0xe5,                         //           mov  rbp, rsp
            0xe8, 0x00, 0x00, 0x00, 0x00,             //           call _leaf
            0x83, 0xc0, 0x00,                         //           add  eax, 0           ; keeps the call from being the last instruction
            0x5d,                                     //           pop  rbp
            0xc3,                                     //           ret
            0x55,                                     // main:     push rbp
            0x48, 0x89, 0xe5,                         //           mov  rbp, rsp
            0xe8, 0x00, 0x00, 0x00, 0x00,             //           call _middle
            0x5d,                                     //           pop  rbp
            0xc3);                                    //           ret
        builder.Global("_callback", text);
        builder.Global("_leaf", text, Leaf);
        builder.Global("_middle", text, Middle);
        builder.Global("_main", text, Main);
        builder.X64(text, Leaf + 18, MachRelocX64.Signed, "_callback");
        builder.X64(text, Leaf + 27, MachRelocX64.Branch, "__Unwind_Backtrace");
        builder.X64(text, Middle + 5, MachRelocX64.Branch, "_leaf");
        builder.X64(text, Main + 5, MachRelocX64.Branch, "_middle");
        builder.UnwindEntry(text, Leaf, 40, FrameEncoding);
        builder.UnwindEntry(text, Main, 11, FrameEncoding);

        var tables = builder.AddSection("__TEXT", "__gcc_except_tab", [1, 2, 3, 4, 5, 6, 7, 8], alignmentLog2: 2);
        builder.Local("GCC_except_table0", tables);
        builder.Local("GCC_except_table1", tables, value: 4);

        // A CIE with a personality and an FDE with language specific data, as a C++ compiler emits them. Only the
        // personality has a relocation; the other addresses are distances that the assembler computed.
        byte[] frames =
        [
            0x1c, 0x00, 0x00, 0x00,                          // CIE: length
            0x00, 0x00, 0x00, 0x00,                          //      this is a CIE
            0x01,                                            //      version
            0x7a, 0x50, 0x4c, 0x52, 0x00,                    //      augmentation "zPLR"
            0x01,                                            //      code alignment factor
            0x78,                                            //      data alignment factor: -8
            0x10,                                            //      return address register
            0x07,                                            //      length of the augmentation data
            0x9b,                                            //      P: through a pointer, relative, 32-bit
            0x00, 0x00, 0x00, 0x00,                          //      personality (GOT entry)
            0x10,                                            //      L: 64-bit, relative to the field
            0x10,                                            //      R: 64-bit, relative to the field
            0x0c, 0x07, 0x08,                                //      DW_CFA_def_cfa rsp, 8
            0x90, 0x01,                                      //      DW_CFA_offset return address, -8
            0x00, 0x00,                                      //      padding
            0x2c, 0x00, 0x00, 0x00,                          // FDE: length
            0x24, 0x00, 0x00, 0x00,                          //      distance to the CIE
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,  //      start of the function
            0x0e, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,  //      length of the function
            0x08,                                            //      length of the augmentation data
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,  //      language specific data
            0x41,                                            //      DW_CFA_advance_loc 1
            0x0e, 0x10,                                      //      DW_CFA_def_cfa_offset 16
            0x86, 0x02,                                      //      DW_CFA_offset rbp, -16
            0x43,                                            //      DW_CFA_advance_loc 3
            0x0d, 0x06,                                      //      DW_CFA_def_cfa_register rbp
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,        //      padding
        ];
        var ehFrame = builder.AddSection("__TEXT", "__eh_frame", frames, MachSectionType.Coalesced, 3);
        builder.X64(ehFrame, 19, MachRelocX64.Got, "___objc_personality_v0");
        LittleEndian.I32(ehFrame.Data, 19, 4);
        builder.Distance(ehFrame, 32 + 8, 8, text, Middle);
        builder.Distance(ehFrame, 32 + 25, 8, tables, 4);
        return builder.Build();
    }
}
