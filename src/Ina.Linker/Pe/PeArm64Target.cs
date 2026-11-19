using Ina.Linker.Elf;

namespace Ina.Linker.Pe;

/// <summary>AArch64 specifics of the PE linker: relocations, import thunks and the generated runtime support.</summary>
internal sealed class PeArm64Target : PeTarget
{
    // Register numbers. x16 and x17 are the scratch registers that code between a call and its target may use.
    private const int X0 = 0, X1 = 1, X2 = 2, X16 = 16, X17 = 17;

    private const uint Ret = 0xd65f03c0;

    /// <summary><c>stp x29, x30, [sp, #-32]! ; mov x29, sp</c>: a frame with room for two locals.</summary>
    private static readonly uint[] Prolog32 = [0xa9be7bfd, 0x910003fd];

    /// <summary><c>ldp x29, x30, [sp], #32 ; ret</c></summary>
    private static readonly uint[] Epilog32 = [0xa8c27bfd, Ret];

    public override ushort Machine => CoffMachine.Arm64;

    public override uint ThunkSize => 16;

    public override uint RuntimeFunctionSize => 8;

    public override ushort Addr64Relocation => RelArm64.Addr64;

    public override void WriteThunk(Span<byte> code, uint thunkRva, uint slotRva)
    {
        var pages = (long)(slotRva >> 12) - (thunkRva >> 12);
        LittleEndian.U32(code, 0, 0x90000010 | (uint)(pages & 3) << 29 | (uint)((pages >> 2) & 0x7ffff) << 5); // adrp x16, slot
        LittleEndian.U32(code, 4, 0xf9400210 | ((slotRva & 0xfff) >> 3) << 10);                               // ldr  x16, [x16, #:lo12:slot]
        LittleEndian.U32(code, 8, 0xd61f0200);                                                                // br   x16
    }

    public override void Apply(PeLinkContext context, CoffSection section, Span<byte> data, in CoffRelocation relocation, PeSymbol symbol)
    {
        var offset = (int)relocation.Offset;
        var size = relocation.Type switch
        {
            RelArm64.Absolute or RelArm64.Token => 0,
            RelArm64.Addr64 => 8,
            RelArm64.Section => 2,
            _ => 4,
        };
        if (offset < 0 || offset + size > data.Length)
        {
            throw new LinkerException($"{section.DisplayName}: relocation at offset 0x{relocation.Offset:x} is outside the section");
        }

        var target = RelativeAddress(context, symbol);
        var place = (long)section.Rva + offset;
        var sectionRelative = target - (symbol.Kind == PeSymbolKind.Defined ? symbol.Chunk?.Output?.Rva ?? 0 : 0);
        switch (relocation.Type)
        {
            case RelArm64.Absolute:
            case RelArm64.Token:
                break;

            case RelArm64.Addr64:
                LittleEndian.U64(data, offset, LittleEndian.U64(data, offset) + context.ImageBase + (ulong)target);
                break;

            case RelArm64.Addr32:
                if (context.IsRelocatable && symbol.Kind == PeSymbolKind.Defined)
                {
                    // The loader can not adjust a 32-bit address of an image that lives above 4 GiB.
                    throw new LinkerException(
                        $"{section.DisplayName}: 32-bit absolute address of '{symbol.Name}' in an image that can be loaded anywhere");
                }

                Add32(data, offset, (long)context.ImageBase + target, section, relocation, symbol);
                break;

            case RelArm64.Addr32Nb:
                Add32(data, offset, target, section, relocation, symbol);
                break;

            case RelArm64.Rel32:
                Add32(data, offset, target - (place + 4), section, relocation, symbol);
                break;

            case RelArm64.SecRel:
                Add32(data, offset, sectionRelative, section, relocation, symbol);
                break;

            case RelArm64.Section:
                LittleEndian.U16(data, offset, (ushort)(symbol.Kind == PeSymbolKind.Defined ? symbol.Chunk?.Output?.Index ?? 0 : 0));
                break;

            case RelArm64.Branch26:
                ApplyBranch(data, offset, target - place, bits: 26, shift: 0, section, relocation, symbol);
                break;

            case RelArm64.Branch19:
                ApplyBranch(data, offset, target - place, bits: 19, shift: 5, section, relocation, symbol);
                break;

            case RelArm64.Branch14:
                ApplyBranch(data, offset, target - place, bits: 14, shift: 5, section, relocation, symbol);
                break;

            case RelArm64.PageBaseRel21:
            case RelArm64.Rel21:
            {
                // ADRP and ADR: the instruction holds the addend as a byte offset. ADRP then takes the distance
                // between the 4 KiB pages of the target and of the instruction, ADR the distance in bytes.
                var instruction = LittleEndian.U32(data, offset);
                var addend = SignExtend(((instruction >> 29) & 3) | ((instruction >> 3) & 0x1ffffc), 21);
                var value = relocation.Type == RelArm64.PageBaseRel21
                    ? ((target + addend) >> 12) - (place >> 12)
                    : target + addend - place;
                if (value is < -(1 << 20) or >= 1 << 20)
                {
                    throw OutOfRange(this, section, relocation, symbol);
                }

                LittleEndian.U32(data, offset, (instruction & 0x9f00001f) | (uint)(value & 3) << 29 | (uint)((value >> 2) & 0x7ffff) << 5);
                break;
            }

            case RelArm64.PageOffset12A:
                AddImmediate12(data, offset, (uint)target & 0xfff, scale: 0);
                break;

            case RelArm64.PageOffset12L:
                AddScaledImmediate12(data, offset, (uint)target & 0xfff, section, relocation, symbol);
                break;

            case RelArm64.SecRelLow12A:
                AddImmediate12(data, offset, (uint)sectionRelative & 0xfff, scale: 0);
                break;

            case RelArm64.SecRelHigh12A:
                if (sectionRelative is < 0 or >= 1 << 24)
                {
                    throw OutOfRange(this, section, relocation, symbol);
                }

                AddImmediate12(data, offset, (uint)sectionRelative >> 12, scale: 0);
                break;

            case RelArm64.SecRelLow12L:
                AddScaledImmediate12(data, offset, (uint)sectionRelative & 0xfff, section, relocation, symbol);
                break;

            default:
                throw Unsupported(this, section, relocation, symbol);
        }
    }

    private static long SignExtend(uint value, int bits) => (long)((ulong)value << (64 - bits)) >> (64 - bits);

    private void Add32(Span<byte> data, int offset, long value, CoffSection section, in CoffRelocation relocation, PeSymbol symbol)
    {
        var result = LittleEndian.I32(data, offset) + value;
        if (result is < int.MinValue or > uint.MaxValue)
        {
            throw OutOfRange(this, section, relocation, symbol);
        }

        LittleEndian.I32(data, offset, (int)result);
    }

    /// <summary>Patches the word-scaled displacement of a branch; the field already holds the addend.</summary>
    private void ApplyBranch(
        Span<byte> data, int offset, long distance, int bits, int shift, CoffSection section, in CoffRelocation relocation, PeSymbol symbol)
    {
        var instruction = LittleEndian.U32(data, offset);
        var mask = ((1u << bits) - 1) << shift;
        var value = distance + (SignExtend((instruction & mask) >> shift, bits) << 2);
        if ((value & 3) != 0 || value < -(1L << (bits + 1)) || value >= 1L << (bits + 1))
        {
            throw OutOfRange(this, section, relocation, symbol);
        }

        LittleEndian.U32(data, offset, (instruction & ~mask) | ((uint)(value >> 2) << shift & mask));
    }

    /// <summary>
    /// Adds to the 12-bit immediate of an ADD or a load/store instruction. Only the low bits are kept: the value
    /// is the offset inside a page, and the page itself comes from the ADRP that precedes the instruction.
    /// </summary>
    private static void AddImmediate12(Span<byte> data, int offset, uint value, int scale)
    {
        var instruction = LittleEndian.U32(data, offset);
        var immediate = (value + ((instruction >> 10) & 0xfff)) & (0xfffu >> scale);
        LittleEndian.U32(data, offset, (instruction & ~(0xfffu << 10)) | immediate << 10);
    }

    /// <summary>Like <see cref="AddImmediate12"/> for loads and stores, whose immediate counts in units of the access size.</summary>
    private void AddScaledImmediate12(Span<byte> data, int offset, uint value, CoffSection section, in CoffRelocation relocation, PeSymbol symbol)
    {
        var instruction = LittleEndian.U32(data, offset);
        var scale = (int)(instruction >> 30);

        // Loads and stores of 128-bit vector registers encode their size differently.
        if ((instruction & 0x0480_0000) == 0x0480_0000)
        {
            scale += 4;
        }

        if ((value & ((1u << scale) - 1)) != 0)
        {
            throw new LinkerException(
                $"{section.DisplayName}+0x{relocation.Offset:x}: '{symbol.Name}' is not aligned for the instruction that accesses it");
        }

        AddImmediate12(data, offset, value >> scale, scale);
    }

    public override string RelocationName(ushort type) => type switch
    {
        RelArm64.Absolute => "IMAGE_REL_ARM64_ABSOLUTE",
        RelArm64.Addr32 => "IMAGE_REL_ARM64_ADDR32",
        RelArm64.Addr32Nb => "IMAGE_REL_ARM64_ADDR32NB",
        RelArm64.Branch26 => "IMAGE_REL_ARM64_BRANCH26",
        RelArm64.PageBaseRel21 => "IMAGE_REL_ARM64_PAGEBASE_REL21",
        RelArm64.Rel21 => "IMAGE_REL_ARM64_REL21",
        RelArm64.PageOffset12A => "IMAGE_REL_ARM64_PAGEOFFSET_12A",
        RelArm64.PageOffset12L => "IMAGE_REL_ARM64_PAGEOFFSET_12L",
        RelArm64.SecRel => "IMAGE_REL_ARM64_SECREL",
        RelArm64.SecRelLow12A => "IMAGE_REL_ARM64_SECREL_LOW12A",
        RelArm64.SecRelHigh12A => "IMAGE_REL_ARM64_SECREL_HIGH12A",
        RelArm64.SecRelLow12L => "IMAGE_REL_ARM64_SECREL_LOW12L",
        RelArm64.Token => "IMAGE_REL_ARM64_TOKEN",
        RelArm64.Section => "IMAGE_REL_ARM64_SECTION",
        RelArm64.Addr64 => "IMAGE_REL_ARM64_ADDR64",
        RelArm64.Branch19 => "IMAGE_REL_ARM64_BRANCH19",
        RelArm64.Branch14 => "IMAGE_REL_ARM64_BRANCH14",
        RelArm64.Rel32 => "IMAGE_REL_ARM64_REL32",
        _ => $"IMAGE_REL_ARM64 0x{type:x}",
    };

    private static byte[] Encode(params uint[] instructions)
    {
        var code = new byte[instructions.Length * 4];
        for (var i = 0; i < instructions.Length; i++)
        {
            LittleEndian.U32(code, i * 4, instructions[i]);
        }

        return code;
    }

    /// <summary>
    /// Packed unwind data of a function that starts with <see cref="Prolog32"/> and ends with
    /// <see cref="Epilog32"/>: flag 1 (packed), the length in instructions, no saved registers besides the
    /// frame chain (CR = 3), and the frame size in units of 16 bytes.
    /// </summary>
    private static byte[] PackedFrame32(int instructions) => Encode(1u | (uint)instructions << 2 | 3u << 21 | 2u << 23);

    protected override void AddUnwindData(CoffObjectBuilder builder, CoffObjectBuilder.Section text, PeFunction function)
    {
        // The function table entry: the start of the function, and the packed unwind data itself.
        var runtimeFunction = builder.ReadOnlyData(".pdata", [0, 0, 0, 0, .. function.UnwindData!], 4);
        builder.Relocate(runtimeFunction, 0, RelArm64.Addr32Nb, text);
    }

    protected override PeFunction CreateEntryPoint(PeLinkOptions options)
    {
        // See the x64 version for what the startup code does. The local at [sp, #16] holds argc across a call.
        uint[] code =
        [
            .. Prolog32,
            options.Subsystem == PeSubsystem.Console ? 0x52800020u : 0x52800040u, // 08: mov  w0, #<application type>
            0x94000000,                                                             // 0c: bl   _set_app_type
            0x52800020,                                                             // 10: mov  w0, #1          ; _crt_argv_unexpanded_arguments
            0x94000000,                                                             // 14: bl   _configure_wide_argv
            0x90000000,                                                             // 18: adrp x0, __xi_a
            0x91000000,                                                             // 1c: add  x0, x0, :lo12:__xi_a
            0x90000001,                                                             // 20: adrp x1, __xi_z
            0x91000021,                                                             // 24: add  x1, x1, :lo12:__xi_z
            0x94000000,                                                             // 28: bl   _initterm_e     ; C initializers, may fail
            0x35000260,                                                             // 2c: cbnz w0, failed
            0x90000000,                                                             // 30: adrp x0, __xc_a
            0x91000000,                                                             // 34: add  x0, x0, :lo12:__xc_a
            0x90000001,                                                             // 38: adrp x1, __xc_z
            0x91000021,                                                             // 3c: add  x1, x1, :lo12:__xc_z
            0x94000000,                                                             // 40: bl   _initterm       ; C++ initializers
            0xd2800000,                                                             // 44: mov  x0, #0
            0x52800041,                                                             // 48: mov  w1, #2          ; DLL_THREAD_ATTACH
            0xd2800002,                                                             // 4c: mov  x2, #0
            0x94000000,                                                             // 50: bl   __dyn_tls_init  ; thread_local initializers of this thread
            0x94000000,                                                             // 54: bl   __p___argc
            0xb9400008,                                                             // 58: ldr  w8, [x0]
            0xb90013e8,                                                             // 5c: str  w8, [sp, #16]
            0x94000000,                                                             // 60: bl   __p___wargv
            0xf9400001,                                                             // 64: ldr  x1, [x0]
            0xb94013e0,                                                             // 68: ldr  w0, [sp, #16]
            0x94000000,                                                             // 6c: bl   wmain
            0x94000000,                                                             // 70: bl   exit            ; runs the exit handlers, does not return
            0xd43e0000,                                                             // 74: brk  #0xf000
            0x52801fe0,                                                             // 78: failed: mov w0, #255
            .. Epilog32,
        ];

        return new PeFunction(
            "wmainCRTStartup",
            Encode(code),
            [
                (0x0c, RelArm64.Branch26, "_set_app_type"), (0x14, RelArm64.Branch26, "_configure_wide_argv"),
                (0x18, RelArm64.PageBaseRel21, "__xi_a"), (0x1c, RelArm64.PageOffset12A, "__xi_a"),
                (0x20, RelArm64.PageBaseRel21, "__xi_z"), (0x24, RelArm64.PageOffset12A, "__xi_z"),
                (0x28, RelArm64.Branch26, "_initterm_e"),
                (0x30, RelArm64.PageBaseRel21, "__xc_a"), (0x34, RelArm64.PageOffset12A, "__xc_a"),
                (0x38, RelArm64.PageBaseRel21, "__xc_z"), (0x3c, RelArm64.PageOffset12A, "__xc_z"),
                (0x40, RelArm64.Branch26, "_initterm"), (0x50, RelArm64.Branch26, "__dyn_tls_init"), (0x54, RelArm64.Branch26, "__p___argc"),
                (0x60, RelArm64.Branch26, "__p___wargv"), (0x6c, RelArm64.Branch26, options.MainSymbol), (0x70, RelArm64.Branch26, "exit"),
            ])
        {
            UnwindData = PackedFrame32(code.Length),
        };
    }

    protected override PeFunction[] CreateThreadLocalInitializers()
    {
        // See the x64 version. x18 points to the thread environment block, which holds the TLS array at 0x58.
        uint[] code =
        [
            .. Prolog32,
            0x7100083f, // 08: cmp  w1, #2                         ; DLL_THREAD_ATTACH
            0x54000321, // 0c: b.ne done
            0x90000008, // 10: adrp x8, _tls_index
            0xb9400108, // 14: ldr  w8, [x8, :lo12:_tls_index]
            0xf9402e49, // 18: ldr  x9, [x18, #0x58]               ; the TLS array of the thread
            0xf8687929, // 1c: ldr  x9, [x9, x8, lsl #3]           ; the TLS block of this module
            0x91400129, // 20: add  x9, x9, #:hi12:__tls_guard, lsl #12
            0x91000129, // 24: add  x9, x9, #:lo12:__tls_guard
            0x3940012a, // 28: ldrb w10, [x9]
            0x3500022a, // 2c: cbnz w10, done
            0x5280002a, // 30: mov  w10, #1
            0x3900012a, // 34: strb w10, [x9]
            0x90000008, // 38: adrp x8, __xd_a
            0x91000108, // 3c: add  x8, x8, :lo12:__xd_a
            0xf9000be8, // 40: str  x8, [sp, #16]
            0xf9400be8, // 44: next: ldr x8, [sp, #16]
            0x91002108, // 48: add  x8, x8, #8
            0xf9000be8, // 4c: str  x8, [sp, #16]
            0x90000009, // 50: adrp x9, __xd_z
            0x91000129, // 54: add  x9, x9, :lo12:__xd_z
            0xeb09011f, // 58: cmp  x8, x9
            0x540000a2, // 5c: b.hs done
            0xf9400108, // 60: ldr  x8, [x8]
            0xb4ffff08, // 64: cbz  x8, next
            0xd63f0100, // 68: blr  x8
            0x17fffff6, // 6c: b    next
            .. Epilog32, // 70: done:
        ];
        var initialize = new PeFunction(
            "__dyn_tls_init",
            Encode(code),
            [
                (0x10, RelArm64.PageBaseRel21, "_tls_index"), (0x14, RelArm64.PageOffset12L, "_tls_index"),
                (0x20, RelArm64.SecRelHigh12A, "__tls_guard"), (0x24, RelArm64.SecRelLow12A, "__tls_guard"),
                (0x38, RelArm64.PageBaseRel21, "__xd_a"), (0x3c, RelArm64.PageOffset12A, "__xd_a"),
                (0x50, RelArm64.PageBaseRel21, "__xd_z"), (0x54, RelArm64.PageOffset12A, "__xd_z"),
            ])
        {
            UnwindData = PackedFrame32(code.Length),
        };

        var onDemand = new PeFunction(
            "__dyn_tls_on_demand_init",
            Encode(
                0xd2800000,  // mov x0, #0
                0x52800041,  // mov w1, #2
                0xd2800002,  // mov x2, #0
                0x14000000), // b   __dyn_tls_init
            [(0x0c, RelArm64.Branch26, "__dyn_tls_init")]);

        return [initialize, onDemand];
    }

    protected override PeFunction[] CreateStackProtection()
    {
        const uint LoadCookieHigh = 0x90000011; // adrp x17, __security_cookie
        const uint LoadCookieLow = 0xf9400231;  // ldr  x17, [x17, :lo12:__security_cookie]
        const uint FailFast = 0xd43e0060;       // brk  #0xf003     ; __fastfail, with the reason in x0

        return
        [
            // Protected functions call this at the end of their prolog. It reserves 16 bytes of stack and stores
            // the cookie in the upper half; the function may use the lower half. Only x16 and x17 may be changed,
            // because the arguments of the function are still in their registers.
            new PeFunction(
                "__security_push_cookie",
                Encode(
                    LoadCookieHigh,
                    LoadCookieLow,
                    0xd10043ff, // sub sp, sp, #16
                    0xf90007f1, // str x17, [sp, #8]
                    Ret),
                [(0x00, RelArm64.PageBaseRel21, "__security_cookie"), (0x04, RelArm64.PageOffset12L, "__security_cookie")]),

            // The counterpart at the start of the epilog: a difference means the stack was overwritten.
            new PeFunction(
                "__security_pop_cookie",
                Encode(
                    0xf94007f0, // 00: ldr  x16, [sp, #8]
                    0x910043ff, // 04: add  sp, sp, #16
                    LoadCookieHigh,
                    LoadCookieLow,
                    0xeb11021f, // 10: cmp  x16, x17
                    0x54000041, // 14: b.ne corrupted
                    Ret,
                    0x52800040, // 1c: corrupted: mov w0, #2    ; FAST_FAIL_STACK_COOKIE_CHECK_FAILURE
                    FailFast),
                [(0x08, RelArm64.PageBaseRel21, "__security_cookie"), (0x0c, RelArm64.PageOffset12L, "__security_cookie")]),

            // void __security_check_cookie(uintptr_t cookie), for code that checks a cookie it loaded itself.
            new PeFunction(
                "__security_check_cookie",
                Encode(
                    LoadCookieHigh,
                    LoadCookieLow,
                    0xeb11001f, // 08: cmp  x0, x17
                    0x54000041, // 0c: b.ne corrupted
                    Ret,
                    0x52800040, // 14: corrupted: mov w0, #2
                    FailFast),
                [(0x00, RelArm64.PageBaseRel21, "__security_cookie"), (0x04, RelArm64.PageOffset12L, "__security_cookie")]),

            // See the x64 version: lets the unwind continue.
            new PeFunction("__GSHandlerCheck", Encode(0x52800020, Ret), []),                  // mov w0, #1 ; ret
            new PeFunction("__report_rangecheckfailure", Encode(0x52800100, FailFast), []),   // mov w0, #8 ; brk #0xf003
        ];
    }

    protected override PeFunction CreateStackProbe() =>

        // __chkstk: x15 holds the size of the frame that the caller is about to allocate, in units of 16 bytes.
        // Every page of it is touched from the top down. Only x16 and x17 are changed; the caller uses x15 again.
        new(
            "__chkstk",
            Encode(
                0xd37cedf0,  // 00: lsl  x16, x15, #4          ; bytes left to probe
                0x910003f1,  // 04: mov  x17, sp
                0xf140061f,  // 08: page: cmp x16, #0x1000
                0x540000a3,  // 0c: b.lo rest
                0xd1400631,  // 10: sub  x17, x17, #0x1000
                0xf940023f,  // 14: ldr  xzr, [x17]            ; touch the page
                0xd1400610,  // 18: sub  x16, x16, #0x1000
                0x17fffffb,  // 1c: b    page
                0xcb100231,  // 20: rest: sub x17, x17, x16
                0xf940023f,  // 24: ldr  xzr, [x17]
                Ret),
            []);

    protected override (string Pointer, PeFunction Function)[] CreateControlFlowGuardStubs() =>
    [
        // Called with the target of an indirect call in x15; returning means the call may proceed.
        ("__guard_check_icall_fptr", new PeFunction("_guard_check_icall_nop", Encode(Ret), [])),

        // Checks and jumps to the target in x9 in one step.
        ("__guard_dispatch_icall_fptr", new PeFunction("_guard_dispatch_icall_nop", Encode(0xd61f0120), [])), // br x9
    ];

    /// <summary>
    /// The interlocked functions. On x64 the compiler always expands them inline; for ARM64 it calls functions of
    /// the static C runtime, which pick the best instructions for the processor at run time. Ina's versions use
    /// load-exclusive/store-exclusive loops, which every ARMv8 processor supports. They follow the normal calling
    /// convention and return what the Windows functions of the same name return.
    /// </summary>
    protected override IEnumerable<PeFunction> CreateAdditionalFunctions()
    {
        foreach (var (suffix, is64) in new[] { (string.Empty, false), ("64", true), ("Pointer", true) })
        {
            yield return new PeFunction(
                "_InterlockedCompareExchange" + suffix,
                Encode(
                    LoadExclusive(is64, X16, X0),                       // 00: retry: ldaxr x16, [x0]
                    Compare(is64, X16, X2),                             // 04: cmp   x16, x2          ; the comparand
                    0x54000061,                                         // 08: b.ne  done
                    StoreExclusive(is64, X17, X1, X0),                  // 0c: stlxr w17, x1, [x0]    ; fails if someone else wrote
                    RetryIfFailed(X17, -4),                             // 10: cbnz  w17, retry
                    MemoryBarrier,                                      // 14: done: dmb ish
                    Move(is64, X0, X16),                                // 18: mov   x0, x16          ; the previous value
                    Ret),
                []);

            yield return new PeFunction(
                "_InterlockedExchange" + suffix,
                Encode(
                    LoadExclusive(is64, X16, X0),                       // 00: retry: ldaxr x16, [x0]
                    StoreExclusive(is64, X17, X1, X0),                  // 04: stlxr w17, x1, [x0]
                    RetryIfFailed(X17, -2),                             // 08: cbnz  w17, retry
                    MemoryBarrier,
                    Move(is64, X0, X16),                                // the previous value
                    Ret),
                []);
        }

        foreach (var (suffix, is64) in new[] { (string.Empty, false), ("64", true) })
        {
            var size = is64 ? 0x80000000u : 0;

            // Increment and decrement return the new value.
            foreach (var (name, step) in new[] { ("_InterlockedIncrement", 0x11000610u), ("_InterlockedDecrement", 0x51000610u) })
            {
                yield return new PeFunction(
                    name + suffix,
                    Encode(
                        LoadExclusive(is64, X16, X0),                   // 00: retry: ldaxr x16, [x0]
                        size | step,                                    // 04: add/sub x16, x16, #1
                        StoreExclusive(is64, X17, X16, X0),             // 08: stlxr w17, x16, [x0]
                        RetryIfFailed(X17, -3),                         // 0c: cbnz  w17, retry
                        MemoryBarrier,
                        Move(is64, X0, X16),
                        Ret),
                    []);
            }

            yield return new PeFunction(
                "_InterlockedExchangeAdd" + suffix,
                Encode(
                    LoadExclusive(is64, X16, X0),                       // 00: retry: ldaxr x16, [x0]
                    size | 0x0b010210,                                  // 04: add   x16, x16, x1
                    StoreExclusive(is64, X17, X16, X0),                 // 08: stlxr w17, x16, [x0]
                    RetryIfFailed(X17, -3),                             // 0c: cbnz  w17, retry
                    MemoryBarrier,
                    size | 0x4b010200,                                  // 14: sub   x0, x16, x1      ; the previous value
                    Ret),
                []);

            // The bitwise operations return the previous value, so the new one needs a register of its own; the
            // status of the store goes to x2, which carries no argument.
            foreach (var (name, operation) in new[] { ("_InterlockedAnd", 0x0a010211u), ("_InterlockedOr", 0x2a010211u), ("_InterlockedXor", 0x4a010211u) })
            {
                yield return new PeFunction(
                    name + suffix,
                    Encode(
                        LoadExclusive(is64, X16, X0),                   // 00: retry: ldaxr x16, [x0]
                        size | operation,                               // 04: and/orr/eor x17, x16, x1
                        StoreExclusive(is64, X2, X17, X0),              // 08: stlxr w2, x17, [x0]
                        RetryIfFailed(X2, -3),                          // 0c: cbnz  w2, retry
                        MemoryBarrier,
                        Move(is64, X0, X16),
                        Ret),
                    []);
            }
        }
    }

    /// <summary><c>dmb ish</c>: orders the operation with everything before and after it, as Windows promises.</summary>
    private const uint MemoryBarrier = 0xd5033bbf;

    /// <summary><c>ldaxr rt, [rn]</c>: loads and starts watching the location for writes of other processors.</summary>
    private static uint LoadExclusive(bool is64, int rt, int rn) => (is64 ? 0xc85ffc00 : 0x885ffc00) | (uint)rn << 5 | (uint)rt;

    /// <summary><c>stlxr ws, rt, [rn]</c>: stores if nobody else wrote since the load; <c>ws</c> is 0 on success.</summary>
    private static uint StoreExclusive(bool is64, int status, int rt, int rn) =>
        (is64 ? 0xc800fc00 : 0x8800fc00) | (uint)status << 16 | (uint)rn << 5 | (uint)rt;

    /// <summary><c>cmp rn, rm</c></summary>
    private static uint Compare(bool is64, int rn, int rm) => (is64 ? 0xeb00001f : 0x6b00001f) | (uint)rm << 16 | (uint)rn << 5;

    /// <summary><c>mov rd, rm</c></summary>
    private static uint Move(bool is64, int rd, int rm) => (is64 ? 0xaa0003e0 : 0x2a0003e0) | (uint)rm << 16 | (uint)rd;

    /// <summary><c>cbnz w&lt;status&gt;, . + instructions * 4</c></summary>
    private static uint RetryIfFailed(int status, int instructions) => 0x35000000 | ((uint)instructions & 0x7ffff) << 5 | (uint)status;
}
