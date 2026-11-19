using Ina.Linker.Elf;

namespace Ina.Linker.Pe;

/// <summary>x86-64 specifics of the PE linker: relocations, import thunks and the generated runtime support.</summary>
internal sealed class PeX64Target : PeTarget
{
    /// <summary>
    /// Unwind information (<c>UNWIND_INFO</c>) of a function whose prolog is the four byte instruction
    /// <c>sub rsp, 0x38</c>: version 1, one unwind code <c>UWOP_ALLOC_SMALL</c> for (0x38 - 8) / 8 = 6 slots.
    /// </summary>
    private static readonly byte[] Frame38 = [0x01, 0x04, 0x01, 0x00, 0x04, 0x62, 0x00, 0x00];

    public override ushort Machine => CoffMachine.Amd64;

    public override uint ThunkSize => 8;

    public override uint RuntimeFunctionSize => 12;

    public override ushort Addr64Relocation => RelAmd64.Addr64;

    public override void WriteThunk(Span<byte> code, uint thunkRva, uint slotRva)
    {
        // jmp qword ptr [rip + slot] ; int3 ; int3
        code[0] = 0xff;
        code[1] = 0x25;
        LittleEndian.I32(code, 2, (int)(slotRva - (thunkRva + 6)));
        code[6] = 0xcc;
        code[7] = 0xcc;
    }

    public override void Apply(PeLinkContext context, CoffSection section, Span<byte> data, in CoffRelocation relocation, PeSymbol symbol)
    {
        var offset = (int)relocation.Offset;
        var size = relocation.Type switch
        {
            RelAmd64.Absolute => 0,
            RelAmd64.Addr64 => 8,
            RelAmd64.Section => 2,
            _ => 4,
        };
        if (offset < 0 || offset + size > data.Length)
        {
            throw new LinkerException($"{section.DisplayName}: relocation at offset 0x{relocation.Offset:x} is outside the section");
        }

        var target = RelativeAddress(context, symbol);
        var place = (long)section.Rva + offset;
        switch (relocation.Type)
        {
            case RelAmd64.Absolute:
                break;

            case RelAmd64.Addr64:
                LittleEndian.U64(data, offset, LittleEndian.U64(data, offset) + context.ImageBase + (ulong)target);
                break;

            case RelAmd64.Addr32:
                if (context.IsRelocatable && symbol.Kind == PeSymbolKind.Defined)
                {
                    // The loader can not adjust a 32-bit address of an image that lives above 4 GiB.
                    throw new LinkerException(
                        $"{section.DisplayName}: 32-bit absolute address of '{symbol.Name}' in an image that can be loaded anywhere");
                }

                Add32(data, offset, (long)context.ImageBase + target, isSigned: false, section, relocation, symbol);
                break;

            case RelAmd64.Addr32Nb:
                Add32(data, offset, target, isSigned: false, section, relocation, symbol);
                break;

            case >= RelAmd64.Rel32 and <= RelAmd64.Rel32_5:
                // The displacement counts from the end of the instruction, which is 0 to 5 bytes behind the field.
                Add32(data, offset, target - (place + 4 + (relocation.Type - RelAmd64.Rel32)), isSigned: true, section, relocation, symbol);
                break;

            case RelAmd64.Section:
                LittleEndian.U16(data, offset, (ushort)(symbol.Kind == PeSymbolKind.Defined ? symbol.Chunk?.Output?.Index ?? 0 : 0));
                break;

            case RelAmd64.SecRel:
                var sectionStart = symbol.Kind == PeSymbolKind.Defined ? symbol.Chunk?.Output?.Rva ?? 0 : 0;
                Add32(data, offset, target - sectionStart, isSigned: false, section, relocation, symbol);
                break;

            default:
                throw Unsupported(this, section, relocation, symbol);
        }
    }

    private void Add32(Span<byte> data, int offset, long value, bool isSigned, CoffSection section, in CoffRelocation relocation, PeSymbol symbol)
    {
        var result = LittleEndian.I32(data, offset) + value;
        if (isSigned ? result is < int.MinValue or > int.MaxValue : result is < int.MinValue or > uint.MaxValue)
        {
            throw OutOfRange(this, section, relocation, symbol);
        }

        LittleEndian.I32(data, offset, (int)result);
    }

    public override string RelocationName(ushort type) => type switch
    {
        RelAmd64.Absolute => "IMAGE_REL_AMD64_ABSOLUTE",
        RelAmd64.Addr64 => "IMAGE_REL_AMD64_ADDR64",
        RelAmd64.Addr32 => "IMAGE_REL_AMD64_ADDR32",
        RelAmd64.Addr32Nb => "IMAGE_REL_AMD64_ADDR32NB",
        RelAmd64.Rel32 => "IMAGE_REL_AMD64_REL32",
        >= RelAmd64.Rel32_1 and <= RelAmd64.Rel32_5 => $"IMAGE_REL_AMD64_REL32_{type - RelAmd64.Rel32}",
        RelAmd64.Section => "IMAGE_REL_AMD64_SECTION",
        RelAmd64.SecRel => "IMAGE_REL_AMD64_SECREL",
        _ => $"IMAGE_REL_AMD64 0x{type:x}",
    };

    protected override void AddUnwindData(CoffObjectBuilder builder, CoffObjectBuilder.Section text, PeFunction function)
    {
        var unwind = builder.ReadOnlyData(".xdata", function.UnwindData, 4);

        // RUNTIME_FUNCTION: start and end of the function and its unwind information, relative to the image base.
        var entry = new byte[12];
        LittleEndian.U32(entry, 4, (uint)function.Code.Length);
        var runtimeFunction = builder.ReadOnlyData(".pdata", entry, 4);
        builder.Relocate(runtimeFunction, 0, RelAmd64.Addr32Nb, text);
        builder.Relocate(runtimeFunction, 4, RelAmd64.Addr32Nb, text);
        builder.Relocate(runtimeFunction, 8, RelAmd64.Addr32Nb, unwind);
    }

    protected override PeFunction CreateEntryPoint(PeLinkOptions options)
    {
        // What the startup code of the C runtime does for a program that uses the C runtime DLL, reduced to what
        // a NativeAOT program needs: tell the runtime what kind of program this is, let it parse the command line,
        // run the initializers, and call wmain.
        byte[] code =
        [
            0x48, 0x83, 0xec, 0x38,                   // 00: sub  rsp, 0x38
            0xb9, 0x00, 0x00, 0x00, 0x00,             // 04: mov  ecx, <application type>
            0xe8, 0x00, 0x00, 0x00, 0x00,             // 09: call _set_app_type
            0xb9, 0x01, 0x00, 0x00, 0x00,             // 0e: mov  ecx, 1                  ; _crt_argv_unexpanded_arguments
            0xe8, 0x00, 0x00, 0x00, 0x00,             // 13: call _configure_wide_argv
            0x48, 0x8d, 0x0d, 0x00, 0x00, 0x00, 0x00, // 18: lea  rcx, [rip + __xi_a]
            0x48, 0x8d, 0x15, 0x00, 0x00, 0x00, 0x00, // 1f: lea  rdx, [rip + __xi_z]
            0xe8, 0x00, 0x00, 0x00, 0x00,             // 26: call _initterm_e             ; C initializers, may fail
            0x85, 0xc0,                               // 2b: test eax, eax
            0x75, 0x46,                               // 2d: jne  failed
            0x48, 0x8d, 0x0d, 0x00, 0x00, 0x00, 0x00, // 2f: lea  rcx, [rip + __xc_a]
            0x48, 0x8d, 0x15, 0x00, 0x00, 0x00, 0x00, // 36: lea  rdx, [rip + __xc_z]
            0xe8, 0x00, 0x00, 0x00, 0x00,             // 3d: call _initterm               ; C++ initializers
            0x33, 0xc9,                               // 42: xor  ecx, ecx
            0xba, 0x02, 0x00, 0x00, 0x00,             // 44: mov  edx, 2                  ; DLL_THREAD_ATTACH
            0x45, 0x33, 0xc0,                         // 49: xor  r8d, r8d
            0xe8, 0x00, 0x00, 0x00, 0x00,             // 4c: call __dyn_tls_init          ; thread_local initializers of this thread
            0xe8, 0x00, 0x00, 0x00, 0x00,             // 51: call __p___argc
            0x8b, 0x00,                               // 56: mov  eax, [rax]
            0x89, 0x44, 0x24, 0x20,                   // 58: mov  [rsp + 0x20], eax
            0xe8, 0x00, 0x00, 0x00, 0x00,             // 5c: call __p___wargv
            0x48, 0x8b, 0x10,                         // 61: mov  rdx, [rax]
            0x8b, 0x4c, 0x24, 0x20,                   // 64: mov  ecx, [rsp + 0x20]
            0xe8, 0x00, 0x00, 0x00, 0x00,             // 68: call wmain
            0x8b, 0xc8,                               // 6d: mov  ecx, eax
            0xe8, 0x00, 0x00, 0x00, 0x00,             // 6f: call exit                    ; runs the exit handlers, does not return
            0xcc,                                     // 74: int3
            0xb8, 0xff, 0x00, 0x00, 0x00,             // 75: failed: mov eax, 255
            0x48, 0x83, 0xc4, 0x38,                   // 7a: add  rsp, 0x38
            0xc3,                                     // 7e: ret
        ];

        // _crt_console_app or _crt_gui_app: decides whether the C runtime reports errors on stderr or in a dialog.
        code[5] = options.Subsystem == PeSubsystem.Console ? (byte)1 : (byte)2;
        return new PeFunction(
            "wmainCRTStartup",
            code,
            [
                (0x0a, RelAmd64.Rel32, "_set_app_type"), (0x14, RelAmd64.Rel32, "_configure_wide_argv"), (0x1b, RelAmd64.Rel32, "__xi_a"),
                (0x22, RelAmd64.Rel32, "__xi_z"), (0x27, RelAmd64.Rel32, "_initterm_e"), (0x32, RelAmd64.Rel32, "__xc_a"),
                (0x39, RelAmd64.Rel32, "__xc_z"), (0x3e, RelAmd64.Rel32, "_initterm"), (0x4d, RelAmd64.Rel32, "__dyn_tls_init"),
                (0x52, RelAmd64.Rel32, "__p___argc"), (0x5d, RelAmd64.Rel32, "__p___wargv"), (0x69, RelAmd64.Rel32, options.MainSymbol),
                (0x70, RelAmd64.Rel32, "exit"),
            ])
        {
            UnwindData = Frame38,
        };
    }

    protected override PeFunction[] CreateThreadLocalInitializers()
    {
        // void __dyn_tls_init(void*, DWORD reason, void*): on DLL_THREAD_ATTACH, runs the functions between __xd_a
        // and __xd_z once per thread; __tls_guard remembers that it happened.
        var initialize = new PeFunction(
            "__dyn_tls_init",
            [
                0x48, 0x83, 0xec, 0x38,                               // 00: sub  rsp, 0x38
                0x83, 0xfa, 0x02,                                     // 04: cmp  edx, 2                ; DLL_THREAD_ATTACH
                0x75, 0x55,                                           // 07: jne  done
                0x8b, 0x05, 0x00, 0x00, 0x00, 0x00,                   // 09: mov  eax, [rip + _tls_index]
                0x65, 0x48, 0x8b, 0x0c, 0x25, 0x58, 0x00, 0x00, 0x00, // 0f: mov  rcx, gs:[0x58]        ; the TLS array of the thread
                0x48, 0x8b, 0x0c, 0xc1,                               // 18: mov  rcx, [rcx + rax * 8]  ; the TLS block of this module
                0x80, 0xb9, 0x00, 0x00, 0x00, 0x00, 0x00,             // 1c: cmp  byte ptr [rcx + __tls_guard], 0
                0x75, 0x39,                                           // 23: jne  done
                0xc6, 0x81, 0x00, 0x00, 0x00, 0x00, 0x01,             // 25: mov  byte ptr [rcx + __tls_guard], 1
                0x48, 0x8d, 0x05, 0x00, 0x00, 0x00, 0x00,             // 2c: lea  rax, [rip + __xd_a]
                0x48, 0x89, 0x44, 0x24, 0x20,                         // 33: mov  [rsp + 0x20], rax
                0x48, 0x8b, 0x44, 0x24, 0x20,                         // 38: next: mov rax, [rsp + 0x20]
                0x48, 0x83, 0xc0, 0x08,                               // 3d: add  rax, 8
                0x48, 0x89, 0x44, 0x24, 0x20,                         // 41: mov  [rsp + 0x20], rax
                0x48, 0x8d, 0x0d, 0x00, 0x00, 0x00, 0x00,             // 46: lea  rcx, [rip + __xd_z]
                0x48, 0x3b, 0xc1,                                     // 4d: cmp  rax, rcx
                0x73, 0x0c,                                           // 50: jae  done
                0x48, 0x8b, 0x00,                                     // 52: mov  rax, [rax]
                0x48, 0x85, 0xc0,                                     // 55: test rax, rax
                0x74, 0xde,                                           // 58: je   next
                0xff, 0xd0,                                           // 5a: call rax
                0xeb, 0xda,                                           // 5c: jmp  next
                0x48, 0x83, 0xc4, 0x38,                               // 5e: done: add rsp, 0x38
                0xc3,                                                 // 62: ret
            ],
            [
                (0x0b, RelAmd64.Rel32, "_tls_index"), (0x1e, RelAmd64.SecRel, "__tls_guard"), (0x27, RelAmd64.SecRel, "__tls_guard"),
                (0x2f, RelAmd64.Rel32, "__xd_a"), (0x49, RelAmd64.Rel32, "__xd_z"),
            ])
        {
            UnwindData = Frame38,
        };

        // void __dyn_tls_on_demand_init(): what compiled code calls before the first use of a thread_local variable.
        var onDemand = new PeFunction(
            "__dyn_tls_on_demand_init",
            [
                0x33, 0xc9,                   // 00: xor ecx, ecx
                0xba, 0x02, 0x00, 0x00, 0x00, // 02: mov edx, 2
                0x45, 0x33, 0xc0,             // 07: xor r8d, r8d
                0xe9, 0x00, 0x00, 0x00, 0x00, // 0a: jmp __dyn_tls_init
            ],
            [(0x0b, RelAmd64.Rel32, "__dyn_tls_init")]);

        return [initialize, onDemand];
    }

    protected override PeFunction[] CreateStackProtection() =>
    [
        // void __security_check_cookie(uintptr_t cookie): called at the end of a protected function with the value
        // it saved on entry; a difference means the stack was overwritten.
        new PeFunction(
            "__security_check_cookie",
            [
                0x48, 0x3b, 0x0d, 0x00, 0x00, 0x00, 0x00, // 00: cmp  rcx, [rip + __security_cookie]
                0x75, 0x01,                               // 07: jne  corrupted
                0xc3,                                     // 09: ret
                0xb9, 0x02, 0x00, 0x00, 0x00,             // 0a: corrupted: mov ecx, 2   ; FAST_FAIL_STACK_COOKIE_CHECK_FAILURE
                0xcd, 0x29,                               // 0f: int  0x29               ; __fastfail
                0xcc,                                     // 11: int3
            ],
            [(0x03, RelAmd64.Rel32, "__security_cookie")]),

        // The exception handler of functions that are protected but have no handler of their own. The Visual C++
        // runtime validates the cookie of the frame here as well; Ina's version only lets the unwind continue.
        new PeFunction(
            "__GSHandlerCheck",
            [
                0xb8, 0x01, 0x00, 0x00, 0x00, // mov eax, 1   ; ExceptionContinueSearch
                0xc3,                         // ret
            ],
            []),

        // Called when a buffer index that the compiler checks is out of range.
        new PeFunction(
            "__report_rangecheckfailure",
            [
                0xb9, 0x08, 0x00, 0x00, 0x00, // mov ecx, 8   ; FAST_FAIL_RANGE_CHECK_FAILURE
                0xcd, 0x29,                   // int 0x29
                0xcc,                         // int3
            ],
            []),
    ];

    protected override PeFunction CreateStackProbe() =>

        // __chkstk: rax holds the size of the frame that the caller is about to allocate. Windows grows the stack
        // one guard page at a time, so every page of the new frame has to be touched in order, from the top down.
        // All registers except r10 and r11 are preserved.
        new(
            "__chkstk",
            [
                0x4c, 0x8d, 0x54, 0x24, 0x08,             // 00: lea  r10, [rsp + 8]     ; the stack pointer of the caller
                0x4c, 0x8b, 0xd8,                         // 05: mov  r11, rax           ; bytes left to probe
                0x49, 0x81, 0xfb, 0x00, 0x10, 0x00, 0x00, // 08: page: cmp r11, 0x1000
                0x72, 0x14,                               // 0f: jb   rest
                0x49, 0x81, 0xea, 0x00, 0x10, 0x00, 0x00, // 11: sub  r10, 0x1000
                0x41, 0xf6, 0x02, 0x00,                   // 18: test byte ptr [r10], 0  ; touch the page
                0x49, 0x81, 0xeb, 0x00, 0x10, 0x00, 0x00, // 1c: sub  r11, 0x1000
                0xeb, 0xe3,                               // 23: jmp  page
                0x4d, 0x2b, 0xd3,                         // 25: rest: sub r10, r11
                0x41, 0xf6, 0x02, 0x00,                   // 28: test byte ptr [r10], 0
                0xc3,                                     // 2c: ret
            ],
            []);

    protected override (string Pointer, PeFunction Function)[] CreateControlFlowGuardStubs() =>
    [
        // Called with the target of an indirect call in rcx; returning means the call may proceed.
        ("__guard_check_icall_fptr", new PeFunction("_guard_check_icall_nop", [0xc3], [])),

        // Checks and calls the target in rax in one step.
        ("__guard_dispatch_icall_fptr", new PeFunction("_guard_dispatch_icall_nop", [0xff, 0xe0], [])), // jmp rax
    ];
}
