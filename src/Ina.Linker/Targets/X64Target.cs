using Ina.Linker.Abi;
using Ina.Linker.Elf;
using Ina.Linker.Input;
using Ina.Linker.Symbols;

namespace Ina.Linker.Targets;

/// <summary>x86-64 (System V psABI) support.</summary>
internal sealed class X64Target : TargetArch
{
    public override ElfMachine Machine => ElfMachine.X86_64;

    public override ulong PageSize => 0x1000;

    public override ulong DefaultImageBase => 0x400000;

    public override int PltEntrySize => 8;

    public override uint RelativeRelocation => RelocX64.Relative;

    public override uint GlobDatRelocation => RelocX64.GlobDat;

    public override uint Abs64Relocation => RelocX64.Abs64;

    public override void Scan(LinkContext context, Fragment fragment, in Relocation relocation, Symbol symbol)
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

    public override void Apply(LinkContext context, Fragment fragment, Span<byte> data, in Relocation relocation, Symbol symbol)
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
                Write32(data, offset, (long)(address - context.TlsStart) + addend, fragment, relocation, symbol);
                break;

            case RelocX64.DtpOff64:
                LittleEndian.U64(data, offset, address - context.TlsStart + (ulong)addend);
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

    public override long ThreadPointerOffset(LinkContext context, ulong address)
    {
        // Variant II: the TLS block ends at the thread pointer, so offsets are negative.
        var blockSize = (context.TlsSize + context.TlsAlignment - 1) & ~(context.TlsAlignment - 1);
        return (long)(address - context.TlsStart) - (long)blockSize;
    }

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
