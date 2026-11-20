using Ina.Linker.Abi;

namespace Ina.Linker.Elf;

/// <summary>AArch64 (Arm 64-bit, little-endian) support.</summary>
internal sealed class ElfArm64Target : ElfTarget
{
    private const uint Nop = 0xd503201f;

    public override ElfMachine Machine => ElfMachine.AArch64;

    // Kernels may be configured for 64 KiB pages, so the image has to be loadable with that granularity.
    public override ulong PageSize => 0x10000;

    public override ulong DefaultImageBase => 0x400000;

    public override int PltEntrySize => 16;

    public override uint RelativeRelocation => RelocArm64.Relative;

    public override uint GlobDatRelocation => RelocArm64.GlobDat;

    public override uint Abs64Relocation => RelocArm64.Abs64;

    public override void Scan(ElfLinkContext context, Fragment fragment, in Relocation relocation, Symbol symbol)
    {
        switch (relocation.Type)
        {
            case RelocArm64.None:
                break;

            case RelocArm64.Abs64:
                if (symbol.IsImported)
                {
                    context.AddSymbolicRelocation(fragment, relocation.Offset, symbol, relocation.Addend);
                }
                else if (context.IsPositionIndependent && symbol.IsImageRelative)
                {
                    context.AddRelativeRelocation(fragment, relocation.Offset, symbol, relocation.Addend);
                }

                break;

            case RelocArm64.Abs32:
                if (symbol.IsImported || (context.IsPositionIndependent && symbol.IsImageRelative))
                {
                    throw new LinkerException(
                        $"{fragment.DisplayName}: relocation {RelocationName(relocation.Type)} against '{symbol.Name}' cannot be used in a position independent executable");
                }

                break;

            case RelocArm64.Call26 or RelocArm64.Jump26:
                if (symbol.IsImported)
                {
                    context.Plt.Add(symbol, context);
                }

                break;

            case RelocArm64.Prel32 or RelocArm64.Prel64 or RelocArm64.AdrPrelPgHi21 or RelocArm64.AdrPrelPgHi21Nc or RelocArm64.AdrPrelLo21
                or RelocArm64.AddAbsLo12Nc or RelocArm64.Ldst8AbsLo12Nc or RelocArm64.Ldst16AbsLo12Nc or RelocArm64.Ldst32AbsLo12Nc
                or RelocArm64.Ldst64AbsLo12Nc or RelocArm64.Ldst128AbsLo12Nc or RelocArm64.CondBr19 or RelocArm64.TstBr14 or RelocArm64.LdPrelLo19:
                if (symbol.IsImported)
                {
                    if (symbol.Import!.Kind != ImportedSymbolKind.Function)
                    {
                        // Would need a copy relocation; position independent inputs go through the GOT instead.
                        throw new LinkerException(
                            $"{fragment.DisplayName}: direct reference to imported data symbol '{symbol.Name}' is not supported; compile with -fPIC");
                    }

                    // The address of an imported function taken directly is the address of its stub.
                    context.Plt.Add(symbol, context);
                }

                break;

            case RelocArm64.AdrGotPage or RelocArm64.Ld64GotLo12Nc:
                context.Got.AddAddress(symbol, context);
                break;

            case RelocArm64.TlsIeAdrGotTprelPage21 or RelocArm64.TlsIeLd64GotTprelLo12Nc:
                RequireLocalTls(fragment, relocation, symbol);
                context.Got.AddThreadPointerOffset(symbol);
                break;

            case RelocArm64.TlsDescAdrPage21 or RelocArm64.TlsDescLd64Lo12 or RelocArm64.TlsDescAddLo12 or RelocArm64.TlsDescCall
                or RelocArm64.TlsLeAddTprelHi12 or RelocArm64.TlsLeAddTprelLo12 or RelocArm64.TlsLeAddTprelLo12Nc:
                RequireLocalTls(fragment, relocation, symbol);
                break;

            default:
                throw Unsupported(this, fragment, relocation, symbol);
        }
    }

    public override void Apply(ElfLinkContext context, Fragment fragment, Span<byte> data, in Relocation relocation, Symbol symbol)
    {
        var offset = (int)relocation.Offset;
        var place = fragment.Address + relocation.Offset;
        var addend = relocation.Addend;

        // Calls and direct address computations of imported functions go to the stub.
        var address = symbol.IsImported && symbol.PltIndex >= 0 ? context.Plt.EntryAddress(symbol) : symbol.Address;
        var target = address + (ulong)addend;

        switch (relocation.Type)
        {
            case RelocArm64.None:
                break;

            case RelocArm64.Abs64:
                // For imports the dynamic relocation carries the addend; the field itself stays zero.
                LittleEndian.U64(data, offset, symbol.IsImported ? 0 : symbol.Address + (ulong)addend);
                break;

            case RelocArm64.Abs32:
                if (target > uint.MaxValue)
                {
                    throw OutOfRange(this, fragment, relocation, symbol);
                }

                LittleEndian.U32(data, offset, (uint)target);
                break;

            case RelocArm64.Prel32:
            {
                var value = (long)target - (long)place;
                if (value is < int.MinValue or > int.MaxValue)
                {
                    throw OutOfRange(this, fragment, relocation, symbol);
                }

                LittleEndian.I32(data, offset, (int)value);
                break;
            }

            case RelocArm64.Prel64:
                LittleEndian.U64(data, offset, target - place);
                break;

            case RelocArm64.Call26 or RelocArm64.Jump26:
            {
                // A branch to an unresolved weak symbol falls through to the next instruction.
                var destination = symbol.Kind == SymbolKind.Undefined ? place + 4 : target;
                PatchBranch(data, offset, CheckedDisplacement(destination, place, 28, fragment, relocation, symbol), 26, 0);
                break;
            }

            case RelocArm64.CondBr19 or RelocArm64.LdPrelLo19:
                PatchBranch(data, offset, CheckedDisplacement(target, place, 21, fragment, relocation, symbol), 19, 5);
                break;

            case RelocArm64.TstBr14:
                PatchBranch(data, offset, CheckedDisplacement(target, place, 16, fragment, relocation, symbol), 14, 5);
                break;

            case RelocArm64.AdrPrelLo21:
                PatchAdr(data, offset, CheckedDisplacement(target, place, 21, fragment, relocation, symbol));
                break;

            case RelocArm64.AdrPrelPgHi21 or RelocArm64.AdrPrelPgHi21Nc:
                PatchAdr(data, offset, PageDisplacement(target, place, fragment, relocation, symbol));
                break;

            case RelocArm64.AddAbsLo12Nc or RelocArm64.Ldst8AbsLo12Nc:
                PatchImm12(data, offset, (uint)(target & 0xfff));
                break;

            case RelocArm64.Ldst16AbsLo12Nc:
                PatchScaledImm12(data, offset, target, 1, fragment, relocation, symbol);
                break;

            case RelocArm64.Ldst32AbsLo12Nc:
                PatchScaledImm12(data, offset, target, 2, fragment, relocation, symbol);
                break;

            case RelocArm64.Ldst64AbsLo12Nc:
                PatchScaledImm12(data, offset, target, 3, fragment, relocation, symbol);
                break;

            case RelocArm64.Ldst128AbsLo12Nc:
                PatchScaledImm12(data, offset, target, 4, fragment, relocation, symbol);
                break;

            case RelocArm64.AdrGotPage:
                PatchAdr(data, offset, PageDisplacement(context.Got.EntryAddress(symbol.GotIndex) + (ulong)addend, place, fragment, relocation, symbol));
                break;

            case RelocArm64.Ld64GotLo12Nc:
                PatchScaledImm12(data, offset, context.Got.EntryAddress(symbol.GotIndex) + (ulong)addend, 3, fragment, relocation, symbol);
                break;

            case RelocArm64.TlsIeAdrGotTprelPage21:
                PatchAdr(data, offset, PageDisplacement(context.Got.EntryAddress(symbol.GotTpIndex) + (ulong)addend, place, fragment, relocation, symbol));
                break;

            case RelocArm64.TlsIeLd64GotTprelLo12Nc:
                PatchScaledImm12(data, offset, context.Got.EntryAddress(symbol.GotTpIndex) + (ulong)addend, 3, fragment, relocation, symbol);
                break;

            case RelocArm64.TlsLeAddTprelHi12:
                PatchImm12(data, offset, (uint)((ThreadPointerOffset(context, symbol.Address) + addend) >> 12) & 0xfff);
                break;

            case RelocArm64.TlsLeAddTprelLo12 or RelocArm64.TlsLeAddTprelLo12Nc:
                PatchImm12(data, offset, (uint)(ThreadPointerOffset(context, symbol.Address) + addend) & 0xfff);
                break;

            // The descriptor-based sequence
            //     adrp x0, :tlsdesc:var ; ldr x1, [x0, :tlsdesc_lo12:var] ; add x0, x0, :tlsdesc_lo12:var ; blr x1
            // computes the offset of a variable from the thread pointer in x0. In an executable that offset is a
            // link-time constant, so the sequence is rewritten to load it directly:
            //     movz x0, #hi16, lsl #16 ; movk x0, #lo16 ; nop ; nop
            case RelocArm64.TlsDescAdrPage21:
            {
                var tpOffset = CheckedTlsOffset(context, symbol, addend, fragment, relocation);
                LittleEndian.U32(data, offset, 0xd2a00000 | (((tpOffset >> 16) & 0xffff) << 5));
                break;
            }

            case RelocArm64.TlsDescLd64Lo12:
            {
                var tpOffset = CheckedTlsOffset(context, symbol, addend, fragment, relocation);
                LittleEndian.U32(data, offset, 0xf2800000 | ((tpOffset & 0xffff) << 5));
                break;
            }

            case RelocArm64.TlsDescAddLo12 or RelocArm64.TlsDescCall:
                LittleEndian.U32(data, offset, Nop);
                break;

            default:
                throw Unsupported(this, fragment, relocation, symbol);
        }
    }

    public override void WritePltEntry(Span<byte> entry, ulong entryAddress, ulong gotEntryAddress)
    {
        var pages = (long)(gotEntryAddress & ~0xfffUL) - (long)(entryAddress & ~0xfffUL);
        var immediate = (uint)(pages >> 12);
        var low = (uint)(gotEntryAddress & 0xfff);

        // adrp x16, page(got) ; ldr x17, [x16, lo12(got)] ; add x16, x16, lo12(got) ; br x17
        LittleEndian.U32(entry, 0, 0x90000010 | ((immediate & 3) << 29) | (((immediate >> 2) & 0x7ffff) << 5));
        LittleEndian.U32(entry, 4, 0xf9400211 | ((low >> 3) << 10));
        LittleEndian.U32(entry, 8, 0x91000210 | (low << 10));
        LittleEndian.U32(entry, 12, 0xd61f0220);
    }

    public override SyntheticFunction CreateEntryPoint(string entrySymbol, string mainSymbol)
    {
        // The counterpart of the x86-64 entry point: set up the arguments of __libc_start_main and pass an init
        // function that runs .init_array, so the image works with old and new C libraries alike.
        var code = Words(
            0xd280001d, // _start: mov  x29, #0                 ; mark the outermost frame
            0xd280001e, //         mov  x30, #0
            0xaa0003e5, //         mov  x5, x0                  ; rtld_fini
            0xf94003e1, //         ldr  x1, [sp]                ; argc
            0x910023e2, //         add  x2, sp, #8              ; argv
            0x910003e6, //         mov  x6, sp                  ; stack_end
            0x90000000, //         adrp x0, main
            0x91000000, //         add  x0, x0, :lo12:main
            0x10000103, //         adr  x3, init
            Nop,
            0xd2800004, //         mov  x4, #0                  ; fini
            0x94000000, //         bl   __libc_start_main
            0xd4200020, //         brk  #1                      ; not reached
            Nop,
            Nop,
            Nop,
            0xa9bc7bfd, // init:   stp  x29, x30, [sp, #-64]!   ; (argc, argv, envp)
            0x910003fd, //         mov  x29, sp
            0xa90153f3, //         stp  x19, x20, [sp, #16]
            0xa9025bf5, //         stp  x21, x22, [sp, #32]
            0xf9001bf7, //         str  x23, [sp, #48]
            0x2a0003f5, //         mov  w21, w0
            0xaa0103f6, //         mov  x22, x1
            0xaa0203f7, //         mov  x23, x2
            0x90000013, //         adrp x19, __init_array_start
            0x91000273, //         add  x19, x19, :lo12:__init_array_start
            0x90000014, //         adrp x20, __init_array_end
            0x91000294, //         add  x20, x20, :lo12:__init_array_end
            0xeb14027f, // loop:   cmp  x19, x20
            0x540000e2, //         b.hs done
            0xf8408668, //         ldr  x8, [x19], #8
            0x2a1503e0, //         mov  w0, w21
            0xaa1603e1, //         mov  x1, x22
            0xaa1703e2, //         mov  x2, x23
            0xd63f0100, //         blr  x8
            0x17fffff9, //         b    loop
            0xf9401bf7, // done:   ldr  x23, [sp, #48]
            0xa9425bf5, //         ldp  x21, x22, [sp, #32]
            0xa94153f3, //         ldp  x19, x20, [sp, #16]
            0xa8c47bfd, //         ldp  x29, x30, [sp], #64
            0xd65f03c0); //        ret

        return new SyntheticFunction(entrySymbol, code,
        [
            new SyntheticRelocation(0x18, RelocArm64.AdrPrelPgHi21, mainSymbol, 0),
            new SyntheticRelocation(0x1c, RelocArm64.AddAbsLo12Nc, mainSymbol, 0),
            new SyntheticRelocation(0x2c, RelocArm64.Call26, "__libc_start_main", 0),
            new SyntheticRelocation(0x60, RelocArm64.AdrPrelPgHi21, "__init_array_start", 0),
            new SyntheticRelocation(0x64, RelocArm64.AddAbsLo12Nc, "__init_array_start", 0),
            new SyntheticRelocation(0x68, RelocArm64.AdrPrelPgHi21, "__init_array_end", 0),
            new SyntheticRelocation(0x6c, RelocArm64.AddAbsLo12Nc, "__init_array_end", 0),
        ]);
    }

    public override IReadOnlyList<SyntheticFunction> CreateCLibraryStubs() =>
    [
        // int atexit(void (*function)(void)) => __cxa_atexit(function, NULL, NULL)
        new SyntheticFunction("atexit", Words(
                0xd2800001, // mov x1, #0
                0xd2800002, // mov x2, #0
                0x14000000), // b   __cxa_atexit
            [new SyntheticRelocation(8, RelocArm64.Jump26, "__cxa_atexit", 0)]),

        // int at_quick_exit(void (*function)(void)) => __cxa_at_quick_exit(function, NULL)
        new SyntheticFunction("at_quick_exit", Words(
                0xd2800001, // mov x1, #0
                0x14000000), // b   __cxa_at_quick_exit
            [new SyntheticRelocation(4, RelocArm64.Jump26, "__cxa_at_quick_exit", 0)]),

        // int pthread_atfork(prepare, parent, child) => __register_atfork(prepare, parent, child, NULL)
        new SyntheticFunction("pthread_atfork", Words(
                0xd2800003, // mov x3, #0
                0x14000000), // b   __register_atfork
            [new SyntheticRelocation(4, RelocArm64.Jump26, "__register_atfork", 0)]),

        // void __clear_cache(void *start, void *end): makes freshly written code visible to instruction fetch.
        // Compilers normally take it from their support library (libgcc or compiler-rt). CTR_EL0 holds the cache
        // line sizes and tells whether the data (IDC) or instruction (DIC) caches need no maintenance.
        new SyntheticFunction("__clear_cache", Words(
                0xd53b0022, //       mrs  x2, ctr_el0
                0x37e00162, //       tbnz w2, #28, sync        ; IDC: no data cache cleaning required
                0xd3504c43, //       ubfx x3, x2, #16, #4      ; log2(words per data cache line)
                0xd2800084, //       mov  x4, #4
                0x9ac32084, //       lsl  x4, x4, x3           ; data cache line size
                0xd1000485, //       sub  x5, x4, #1
                0x8a250005, //       bic  x5, x0, x5           ; start rounded down to a line
                0xeb0100bf, // data: cmp  x5, x1
                0x54000082, //       b.hs sync
                0xd50b7b25, //       dc   cvau, x5
                0x8b0400a5, //       add  x5, x5, x4
                0x17fffffc, //       b    data
                0xd5033b9f, // sync: dsb  ish
                0x37e80162, //       tbnz w2, #29, done        ; DIC: no instruction cache invalidation required
                0x92400c43, //       and  x3, x2, #0xf         ; log2(words per instruction cache line)
                0xd2800084, //       mov  x4, #4
                0x9ac32084, //       lsl  x4, x4, x3
                0xd1000485, //       sub  x5, x4, #1
                0x8a250005, //       bic  x5, x0, x5
                0xeb0100bf, // code: cmp  x5, x1
                0x54000082, //       b.hs done
                0xd50b7525, //       ic   ivau, x5
                0x8b0400a5, //       add  x5, x5, x4
                0x17fffffc, //       b    code
                0xd5033b9f, // done: dsb  ish
                0xd5033fdf, //       isb
                0xd65f03c0), //      ret
            []),
    ];

    public override long ThreadPointerOffset(ElfLinkContext context, ulong address)
    {
        // Variant I: the thread pointer points at a 16 byte control block that is followed by the TLS block.
        var controlBlock = (16 + context.TlsAlignment - 1) & ~(context.TlsAlignment - 1);
        return (long)(address - context.TlsStart) + (long)controlBlock;
    }

    public override string RelocationName(uint type) => type switch
    {
        RelocArm64.None => "R_AARCH64_NONE",
        RelocArm64.Abs64 => "R_AARCH64_ABS64",
        RelocArm64.Abs32 => "R_AARCH64_ABS32",
        RelocArm64.Prel64 => "R_AARCH64_PREL64",
        RelocArm64.Prel32 => "R_AARCH64_PREL32",
        RelocArm64.AdrPrelLo21 => "R_AARCH64_ADR_PREL_LO21",
        RelocArm64.AdrPrelPgHi21 => "R_AARCH64_ADR_PREL_PG_HI21",
        RelocArm64.AddAbsLo12Nc => "R_AARCH64_ADD_ABS_LO12_NC",
        RelocArm64.Jump26 => "R_AARCH64_JUMP26",
        RelocArm64.Call26 => "R_AARCH64_CALL26",
        RelocArm64.CondBr19 => "R_AARCH64_CONDBR19",
        RelocArm64.TstBr14 => "R_AARCH64_TSTBR14",
        RelocArm64.Ldst8AbsLo12Nc => "R_AARCH64_LDST8_ABS_LO12_NC",
        RelocArm64.Ldst16AbsLo12Nc => "R_AARCH64_LDST16_ABS_LO12_NC",
        RelocArm64.Ldst32AbsLo12Nc => "R_AARCH64_LDST32_ABS_LO12_NC",
        RelocArm64.Ldst64AbsLo12Nc => "R_AARCH64_LDST64_ABS_LO12_NC",
        RelocArm64.Ldst128AbsLo12Nc => "R_AARCH64_LDST128_ABS_LO12_NC",
        RelocArm64.AdrGotPage => "R_AARCH64_ADR_GOT_PAGE",
        RelocArm64.Ld64GotLo12Nc => "R_AARCH64_LD64_GOT_LO12_NC",
        RelocArm64.TlsDescAdrPage21 => "R_AARCH64_TLSDESC_ADR_PAGE21",
        RelocArm64.TlsDescLd64Lo12 => "R_AARCH64_TLSDESC_LD64_LO12",
        RelocArm64.TlsDescAddLo12 => "R_AARCH64_TLSDESC_ADD_LO12",
        RelocArm64.TlsDescCall => "R_AARCH64_TLSDESC_CALL",
        _ => $"R_AARCH64_<{type}>",
    };

    private static byte[] Words(params uint[] instructions)
    {
        var code = new byte[instructions.Length * 4];
        for (var i = 0; i < instructions.Length; i++)
        {
            LittleEndian.U32(code, i * 4, instructions[i]);
        }

        return code;
    }

    private void RequireLocalTls(Fragment fragment, in Relocation relocation, Symbol symbol)
    {
        if (symbol.IsImported)
        {
            throw new LinkerException(
                $"{fragment.DisplayName}: relocation {RelocationName(relocation.Type)} against imported thread-local symbol '{symbol.Name}' is not supported");
        }
    }

    private uint CheckedTlsOffset(ElfLinkContext context, Symbol symbol, long addend, Fragment fragment, in Relocation relocation)
    {
        var offset = ThreadPointerOffset(context, symbol.Address) + addend;
        if (offset is < 0 or > uint.MaxValue)
        {
            throw OutOfRange(this, fragment, relocation, symbol);
        }

        return (uint)offset;
    }

    /// <summary>Returns <c>target - place</c> after checking that it fits into a signed field of <paramref name="bits"/> bits.</summary>
    private long CheckedDisplacement(ulong target, ulong place, int bits, Fragment fragment, in Relocation relocation, Symbol symbol)
    {
        var displacement = (long)target - (long)place;
        var limit = 1L << (bits - 1);
        if (displacement < -limit || displacement >= limit)
        {
            throw OutOfRange(this, fragment, relocation, symbol);
        }

        return displacement;
    }

    /// <summary>Distance between the 4 KiB pages of target and place, as used by ADRP (range +-4 GiB).</summary>
    private long PageDisplacement(ulong target, ulong place, Fragment fragment, in Relocation relocation, Symbol symbol)
    {
        var displacement = (long)(target & ~0xfffUL) - (long)(place & ~0xfffUL);
        if (displacement is < -(1L << 32) or >= 1L << 32)
        {
            throw OutOfRange(this, fragment, relocation, symbol);
        }

        return displacement >> 12;
    }

    /// <summary>Stores the 21-bit immediate of ADR/ADRP, which is split into a low 2-bit and a high 19-bit part.</summary>
    private static void PatchAdr(Span<byte> data, int offset, long immediate)
    {
        var value = (uint)immediate;
        var instruction = LittleEndian.U32(data, offset) & 0x9f00001f;
        LittleEndian.U32(data, offset, instruction | ((value & 3) << 29) | (((value >> 2) & 0x7ffff) << 5));
    }

    /// <summary>Stores a word displacement into a branch instruction.</summary>
    private static void PatchBranch(Span<byte> data, int offset, long displacement, int bits, int shift)
    {
        var mask = ((1u << bits) - 1) << shift;
        var instruction = LittleEndian.U32(data, offset) & ~mask;
        LittleEndian.U32(data, offset, instruction | (((uint)(displacement >> 2) << shift) & mask));
    }

    private static void PatchImm12(Span<byte> data, int offset, uint value)
    {
        var instruction = LittleEndian.U32(data, offset) & 0xffc003ff;
        LittleEndian.U32(data, offset, instruction | ((value & 0xfff) << 10));
    }

    /// <summary>Stores the low 12 bits of an address into a load/store whose immediate is scaled by the access size.</summary>
    private void PatchScaledImm12(Span<byte> data, int offset, ulong address, int scale, Fragment fragment, in Relocation relocation, Symbol symbol)
    {
        var low = (uint)(address & 0xfff);
        if ((low & ((1u << scale) - 1)) != 0)
        {
            throw new LinkerException(
                $"{fragment.DisplayName}+0x{relocation.Offset:x}: relocation {RelocationName(relocation.Type)} against '{symbol.Name}' needs an address aligned to {1 << scale} bytes");
        }

        PatchImm12(data, offset, low >> scale);
    }
}
