using Ina.Linker.Abi;

namespace Ina.Linker.Elf;

/// <summary>x86-64 (System V psABI) support.</summary>
internal sealed class ElfX64Target : ElfTarget
{
    public override ElfMachine Machine => ElfMachine.X86_64;

    public override ulong PageSize => 0x1000;

    public override ulong DefaultImageBase => 0x400000;

    public override int PltEntrySize => 8;

    public override uint RelativeRelocation => RelocX64.Relative;

    public override uint GlobDatRelocation => RelocX64.GlobDat;

    public override uint Abs64Relocation => RelocX64.Abs64;

    public override void Scan(ElfLinkContext context, Fragment fragment, in Relocation relocation, Symbol symbol)
    {
        switch (relocation.Type)
        {
            case RelocX64.None:
                break;

            case RelocX64.Abs64:
                if (symbol.IsImported)
                {
                    context.AddSymbolicRelocation(fragment, relocation.Offset, symbol, relocation.Addend);
                }
                else if (context.IsPositionIndependent && symbol.IsImageRelative)
                {
                    context.AddRelativeRelocation(fragment, relocation.Offset, symbol, relocation.Addend);
                }

                break;

            case RelocX64.Abs32 or RelocX64.Abs32S:
                if (symbol.IsImported || (context.IsPositionIndependent && symbol.IsImageRelative))
                {
                    // A 32-bit absolute address cannot be fixed up when the image is loaded at a random address.
                    throw new LinkerException(
                        $"{fragment.DisplayName}: relocation {RelocationName(relocation.Type)} against '{symbol.Name}' cannot be used in a position independent executable");
                }

                break;

            case RelocX64.Pc32 or RelocX64.Pc64 or RelocX64.Plt32:
                if (symbol.IsImported)
                {
                    if (symbol.Import!.Kind != ImportedSymbolKind.Function)
                    {
                        // Would need a copy relocation; position independent inputs go through the GOT instead.
                        throw new LinkerException(
                            $"{fragment.DisplayName}: direct reference to imported data symbol '{symbol.Name}' is not supported; compile with -fPIC");
                    }

                    context.Plt.Add(symbol, context);
                }

                break;

            case RelocX64.GotPcRel or RelocX64.GotPcRelX or RelocX64.RexGotPcRelX:
                context.Got.AddAddress(symbol, context);
                break;

            case RelocX64.TlsGd:
                RequireLocalTls(fragment, relocation, symbol);
                context.Got.AddTlsDescriptor(symbol);
                break;

            case RelocX64.TlsLd:
                context.Got.AddTlsModule();
                break;

            case RelocX64.GotTpOff:
                RequireLocalTls(fragment, relocation, symbol);
                context.Got.AddThreadPointerOffset(symbol);
                break;

            case RelocX64.DtpOff32 or RelocX64.DtpOff64 or RelocX64.TpOff32 or RelocX64.TpOff64:
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
        var address = symbol.Address;

        switch (relocation.Type)
        {
            case RelocX64.None:
                break;

            case RelocX64.Abs64:
                // For imports the dynamic relocation carries the addend; the field itself stays zero.
                LittleEndian.U64(data, offset, symbol.IsImported ? 0 : address + (ulong)addend);
                break;

            case RelocX64.Abs32:
            {
                var value = address + (ulong)addend;
                if (value > uint.MaxValue)
                {
                    throw OutOfRange(this, fragment, relocation, symbol);
                }

                LittleEndian.U32(data, offset, (uint)value);
                break;
            }

            case RelocX64.Abs32S:
                Write32(data, offset, (long)address + addend, fragment, relocation, symbol);
                break;

            case RelocX64.Pc32 or RelocX64.Plt32:
            {
                var target = symbol.IsImported ? context.Plt.EntryAddress(symbol) : address;
                if (symbol.Kind == SymbolKind.Undefined)
                {
                    // An unresolved weak reference: the code must test the address before using it, so any value works.
                    LittleEndian.U32(data, offset, 0);
                    break;
                }

                Write32(data, offset, (long)target + addend - (long)place, fragment, relocation, symbol);
                break;
            }

            case RelocX64.Pc64:
            {
                var target = symbol.IsImported ? context.Plt.EntryAddress(symbol) : address;
                LittleEndian.U64(data, offset, target + (ulong)addend - place);
                break;
            }

            case RelocX64.GotPcRel or RelocX64.GotPcRelX or RelocX64.RexGotPcRelX:
                Write32(data, offset, (long)context.Got.EntryAddress(symbol.GotIndex) + addend - (long)place, fragment, relocation, symbol);
                break;

            case RelocX64.TlsGd:
                Write32(data, offset, (long)context.Got.EntryAddress(symbol.TlsGdIndex) + addend - (long)place, fragment, relocation, symbol);
                break;

            case RelocX64.TlsLd:
                Write32(data, offset, (long)context.Got.EntryAddress(context.Got.TlsModuleIndex) + addend - (long)place, fragment, relocation, symbol);
                break;

            case RelocX64.GotTpOff:
                Write32(data, offset, (long)context.Got.EntryAddress(symbol.GotTpIndex) + addend - (long)place, fragment, relocation, symbol);
                break;

            case RelocX64.DtpOff32:
                Write32(data, offset, ModuleOffset(context, address) + addend, fragment, relocation, symbol);
                break;

            case RelocX64.DtpOff64:
                LittleEndian.I64(data, offset, ModuleOffset(context, address) + addend);
                break;

            case RelocX64.TpOff32:
                Write32(data, offset, ThreadPointerOffset(context, address) + addend, fragment, relocation, symbol);
                break;

            case RelocX64.TpOff64:
                LittleEndian.I64(data, offset, ThreadPointerOffset(context, address) + addend);
                break;

            default:
                throw Unsupported(this, fragment, relocation, symbol);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Thread-local storage relaxation
    //
    // Position independent code reaches thread-local variables by calling __tls_get_addr. In an executable the
    // variables live at a fixed offset from the thread pointer, so the calls can be replaced by thread pointer
    // arithmetic. The psABI defines instruction sequences with padding prefixes exactly so that a linker can rewrite
    // them in place; sequences in any other form are left alone and keep calling the loader.
    // ---------------------------------------------------------------------------------------------------------------

    private const string TlsGetAddr = "__tls_get_addr";

    public override void Prepare(ElfLinkContext context, IEnumerable<Fragment> liveFragments)
    {
        // Offsets of local-dynamic accesses (DTPOFF) are relative to whatever the preceding call returned. Rewriting
        // the calls changes that base for all of them, so it is only done when every call site can be rewritten.
        var sites = 0;
        var canonical = true;
        foreach (var fragment in liveFragments)
        {
            var relocations = fragment.Relocations;
            for (var i = 0; i < relocations.Length; i++)
            {
                if (relocations[i].Type == RelocX64.TlsLd)
                {
                    sites++;
                    canonical &= LocalDynamicCallLength(fragment.Content, fragment, i) != 0;
                }
            }
        }

        context.RelaxLocalDynamicTls = sites > 0 && canonical;
    }

    public override int ScanAt(ElfLinkContext context, Fragment fragment, int index, Symbol symbol)
    {
        var relocation = fragment.Relocations[index];
        if (relocation.Type == RelocX64.TlsGd && IsGeneralDynamicSequence(fragment.Content, fragment, index))
        {
            RequireLocalTls(fragment, relocation, symbol);
            return 2;
        }

        if (relocation.Type == RelocX64.TlsLd && context.RelaxLocalDynamicTls)
        {
            return 2;
        }

        return base.ScanAt(context, fragment, index, symbol);
    }

    public override int ApplyAt(ElfLinkContext context, Fragment fragment, Span<byte> data, int index, Symbol symbol)
    {
        var relocation = fragment.Relocations[index];
        var offset = (int)relocation.Offset;

        if (relocation.Type == RelocX64.TlsGd && IsGeneralDynamicSequence(data, fragment, index))
        {
            //   data16 lea rdi, [rip + x@TLSGD]       ->   mov rax, fs:[0]
            //   data16 data16 rex.W call __tls_get_addr     lea rax, [rax + x@TPOFF]
            ReadOnlySpan<byte> replacement = [0x64, 0x48, 0x8b, 0x04, 0x25, 0x00, 0x00, 0x00, 0x00, 0x48, 0x8d, 0x80];
            replacement.CopyTo(data[(offset - 4)..]);

            // The addend compensates for the instruction pointer of the original lea, which no longer applies.
            Write32(data, offset + 8, ThreadPointerOffset(context, symbol.Address) + relocation.Addend + 4, fragment, relocation, symbol);
            return 2;
        }

        if (relocation.Type == RelocX64.TlsLd && context.RelaxLocalDynamicTls)
        {
            //   lea rdi, [rip + x@TLSLD]      ->   data16 data16 data16 mov rax, fs:[0]
            //   call __tls_get_addr
            // The indirect form of the call is one byte longer and gets one more padding prefix.
            ReadOnlySpan<byte> replacement = LocalDynamicCallLength(data, fragment, index) == 5
                ? [0x66, 0x66, 0x66, 0x64, 0x48, 0x8b, 0x04, 0x25, 0x00, 0x00, 0x00, 0x00]
                : [0x66, 0x66, 0x66, 0x66, 0x64, 0x48, 0x8b, 0x04, 0x25, 0x00, 0x00, 0x00, 0x00];
            replacement.CopyTo(data[(offset - 3)..]);
            return 2;
        }

        return base.ApplyAt(context, fragment, data, index, symbol);
    }

    /// <summary>
    /// Recognizes <c>data16 lea rdi, [rip + x@TLSGD]</c> followed by the padded call to <c>__tls_get_addr</c>.
    /// </summary>
    private static bool IsGeneralDynamicSequence(ReadOnlySpan<byte> code, Fragment fragment, int index)
    {
        var relocations = fragment.Relocations;
        var offset = (int)relocations[index].Offset;
        if (index + 1 >= relocations.Length || offset < 4 || offset + 12 > code.Length)
        {
            return false;
        }

        var call = relocations[index + 1];
        if (call.Offset != relocations[index].Offset + 8 || fragment.File.Symbols[call.SymbolIndex].Name != TlsGetAddr)
        {
            return false;
        }

        if (!code.Slice(offset - 4, 4).SequenceEqual<byte>([0x66, 0x48, 0x8d, 0x3d]))
        {
            return false;
        }

        // Either a direct call (data16 data16 rex.W call rel32) or an indirect one through the GOT (data16 rex.W call [rip + got]).
        var opcode = code.Slice(offset + 4, 4);
        return call.Type switch
        {
            RelocX64.Plt32 or RelocX64.Pc32 => opcode.SequenceEqual<byte>([0x66, 0x66, 0x48, 0xe8]),
            RelocX64.GotPcRel or RelocX64.GotPcRelX => opcode.SequenceEqual<byte>([0x66, 0x48, 0xff, 0x15]),
            _ => false,
        };
    }

    /// <summary>
    /// Recognizes <c>lea rdi, [rip + x@TLSLD]</c> followed by a call to <c>__tls_get_addr</c> and returns the length
    /// of the call instruction (5 for a direct call, 6 for an indirect one), or 0 when the sequence has another form.
    /// </summary>
    private static int LocalDynamicCallLength(ReadOnlySpan<byte> code, Fragment fragment, int index)
    {
        var relocations = fragment.Relocations;
        var offset = (int)relocations[index].Offset;
        if (index + 1 >= relocations.Length || offset < 3 || offset + 10 > code.Length)
        {
            return 0;
        }

        var call = relocations[index + 1];
        if (fragment.File.Symbols[call.SymbolIndex].Name != TlsGetAddr || !code.Slice(offset - 3, 3).SequenceEqual<byte>([0x48, 0x8d, 0x3d]))
        {
            return 0;
        }

        if (call.Offset == relocations[index].Offset + 5 && call.Type is RelocX64.Plt32 or RelocX64.Pc32 && code[offset + 4] == 0xe8)
        {
            return 5;
        }

        if (call.Offset == relocations[index].Offset + 6 && call.Type is RelocX64.GotPcRel or RelocX64.GotPcRelX
            && code[offset + 4] == 0xff && code[offset + 5] == 0x15)
        {
            return 6;
        }

        return 0;
    }

    public override void WritePltEntry(Span<byte> entry, ulong entryAddress, ulong gotEntryAddress)
    {
        // jmp *got(%rip); the remaining two bytes are padding that is never executed.
        entry[0] = 0xff;
        entry[1] = 0x25;
        LittleEndian.I32(entry, 2, checked((int)((long)gotEntryAddress - (long)(entryAddress + 6))));
        entry[6] = 0xcc;
        entry[7] = 0xcc;
    }

    public override SyntheticFunction CreateEntryPoint(string entrySymbol, string mainSymbol)
    {
        // _start:
        //     xor  ebp, ebp                      ; mark the outermost frame
        //     mov  r9, rdx                       ; rtld_fini
        //     pop  rsi                           ; argc
        //     mov  rdx, rsp                      ; argv
        //     and  rsp, -16
        //     push rax
        //     push rsp                           ; stack_end
        //     xor  r8d, r8d                      ; fini
        //     lea  rcx, [rip + init]             ; init
        //     lea  rdi, [rip + main]
        //     call [rip + __libc_start_main@GOT]
        //     hlt
        // init:                                  ; (argc, argv, envp) -> runs .init_array
        //     push r13 / r12 / rbp / rbx ; sub rsp, 8
        //     mov  r13d, edi ; mov r12, rsi ; mov rbp, rdx
        //     lea  rbx, [rip + __init_array_start]
        // loop:
        //     lea  rax, [rip + __init_array_end]
        //     cmp  rbx, rax ; jae done
        //     mov  edi, r13d ; mov rsi, r12 ; mov rdx, rbp
        //     call [rbx] ; add rbx, 8 ; jmp loop
        // done:
        //     add rsp, 8 ; pop rbx / rbp / r12 / r13 ; ret
        //
        // Passing an explicit init function keeps the image working with both old C libraries (which only run
        // constructors through this callback) and new ones (which still call it when it is not null).
        byte[] code =
        [
            0x31, 0xed, 0x49, 0x89, 0xd1, 0x5e, 0x48, 0x89, 0xe2, 0x48, 0x83, 0xe4, 0xf0, 0x50, 0x54, 0x45,
            0x31, 0xc0, 0x48, 0x8d, 0x0d, 0x17, 0x00, 0x00, 0x00, 0x48, 0x8d, 0x3d, 0x00, 0x00, 0x00, 0x00,
            0xff, 0x15, 0x00, 0x00, 0x00, 0x00, 0xf4, 0xcc, 0xcc, 0xcc, 0xcc, 0xcc, 0xcc, 0xcc, 0xcc, 0xcc,
            0x41, 0x55, 0x41, 0x54, 0x55, 0x53, 0x48, 0x83, 0xec, 0x08, 0x41, 0x89, 0xfd, 0x49, 0x89, 0xf4,
            0x48, 0x89, 0xd5, 0x48, 0x8d, 0x1d, 0x00, 0x00, 0x00, 0x00, 0x48, 0x8d, 0x05, 0x00, 0x00, 0x00,
            0x00, 0x48, 0x39, 0xc3, 0x73, 0x11, 0x44, 0x89, 0xef, 0x4c, 0x89, 0xe6, 0x48, 0x89, 0xea, 0xff,
            0x13, 0x48, 0x83, 0xc3, 0x08, 0xeb, 0xe3, 0x48, 0x83, 0xc4, 0x08, 0x5b, 0x5d, 0x41, 0x5c, 0x41,
            0x5d, 0xc3,
        ];

        return new SyntheticFunction(entrySymbol, code,
        [
            new SyntheticRelocation(0x1c, RelocX64.Pc32, mainSymbol, -4),
            new SyntheticRelocation(0x22, RelocX64.GotPcRel, "__libc_start_main", -4),
            new SyntheticRelocation(0x46, RelocX64.Pc32, "__init_array_start", -4),
            new SyntheticRelocation(0x4d, RelocX64.Pc32, "__init_array_end", -4),
        ]);
    }

    public override IReadOnlyList<SyntheticFunction> CreateCLibraryStubs() =>
    [
        // int atexit(void (*function)(void)) => __cxa_atexit(function, NULL, NULL)
        //     xor esi, esi ; xor edx, edx ; jmp __cxa_atexit
        // A null DSO handle is what fixed-address executables pass as well: the handlers run at process exit.
        new SyntheticFunction("atexit", [0x31, 0xf6, 0x31, 0xd2, 0xe9, 0x00, 0x00, 0x00, 0x00],
            [new SyntheticRelocation(5, RelocX64.Plt32, "__cxa_atexit", -4)]),

        // int at_quick_exit(void (*function)(void)) => __cxa_at_quick_exit(function, NULL)
        //     xor esi, esi ; jmp __cxa_at_quick_exit
        new SyntheticFunction("at_quick_exit", [0x31, 0xf6, 0xe9, 0x00, 0x00, 0x00, 0x00],
            [new SyntheticRelocation(3, RelocX64.Plt32, "__cxa_at_quick_exit", -4)]),

        // int pthread_atfork(prepare, parent, child) => __register_atfork(prepare, parent, child, NULL)
        //     xor ecx, ecx ; jmp __register_atfork
        new SyntheticFunction("pthread_atfork", [0x31, 0xc9, 0xe9, 0x00, 0x00, 0x00, 0x00],
            [new SyntheticRelocation(3, RelocX64.Plt32, "__register_atfork", -4)]),
    ];

    public override long ThreadPointerOffset(ElfLinkContext context, ulong address)
    {
        // Variant II: the TLS block ends at the thread pointer, so offsets are negative.
        var blockSize = (context.TlsSize + context.TlsAlignment - 1) & ~(context.TlsAlignment - 1);
        return (long)(address - context.TlsStart) - (long)blockSize;
    }

    /// <summary>
    /// The offset a local-dynamic access adds to the result of its call: relative to the start of the TLS block
    /// when the call is kept, relative to the thread pointer when the call was rewritten.
    /// </summary>
    private long ModuleOffset(ElfLinkContext context, ulong address) =>
        context.RelaxLocalDynamicTls ? ThreadPointerOffset(context, address) : (long)(address - context.TlsStart);

    public override string RelocationName(uint type) => type switch
    {
        RelocX64.None => "R_X86_64_NONE",
        RelocX64.Abs64 => "R_X86_64_64",
        RelocX64.Pc32 => "R_X86_64_PC32",
        RelocX64.Got32 => "R_X86_64_GOT32",
        RelocX64.Plt32 => "R_X86_64_PLT32",
        RelocX64.GotPcRel => "R_X86_64_GOTPCREL",
        RelocX64.Abs32 => "R_X86_64_32",
        RelocX64.Abs32S => "R_X86_64_32S",
        RelocX64.TlsGd => "R_X86_64_TLSGD",
        RelocX64.TlsLd => "R_X86_64_TLSLD",
        RelocX64.DtpOff32 => "R_X86_64_DTPOFF32",
        RelocX64.DtpOff64 => "R_X86_64_DTPOFF64",
        RelocX64.GotTpOff => "R_X86_64_GOTTPOFF",
        RelocX64.TpOff32 => "R_X86_64_TPOFF32",
        RelocX64.TpOff64 => "R_X86_64_TPOFF64",
        RelocX64.Pc64 => "R_X86_64_PC64",
        RelocX64.GotPcRelX => "R_X86_64_GOTPCRELX",
        RelocX64.RexGotPcRelX => "R_X86_64_REX_GOTPCRELX",
        _ => $"R_X86_64_<{type}>",
    };

    private void RequireLocalTls(Fragment fragment, in Relocation relocation, Symbol symbol)
    {
        if (symbol.IsImported)
        {
            throw new LinkerException(
                $"{fragment.DisplayName}: relocation {RelocationName(relocation.Type)} against imported thread-local symbol '{symbol.Name}' is not supported");
        }
    }

    private void Write32(Span<byte> data, int offset, long value, Fragment fragment, in Relocation relocation, Symbol symbol)
    {
        if (value is < int.MinValue or > int.MaxValue)
        {
            throw OutOfRange(this, fragment, relocation, symbol);
        }

        LittleEndian.I32(data, offset, (int)value);
    }
}
