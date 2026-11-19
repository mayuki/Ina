using Ina.Linker.Elf;

namespace Ina.Linker.MachO;

/// <summary>x86-64 (Intel Macs): relocation decoding and instruction patching.</summary>
internal sealed class MachX64Target : MachTarget
{
    private const byte MovLoad = 0x8b; // mov r64, [rip + disp32]
    private const byte Lea = 0x8d;     // lea r64, [rip + disp32]

    public override uint CpuType => MachCpu.X64;

    public override uint CpuSubtype => MachCpu.SubtypeX64All;

    // Apple's linker marks x86-64 executables as using 64-bit libraries (CPU_SUBTYPE_LIB64).
    public override uint ExecutableCpuSubtype => MachCpu.SubtypeX64All | 0x80000000;

    public override string Name => "x86_64";

    public override ulong PageSize => 0x1000;

    public override int StubSize => 6;

    public override ulong StubAlignment => 2;

    public override uint DwarfUnwindMode => CompactUnwind.ModeDwarfX64;

    public override string RelocationName(byte type) => type switch
    {
        MachRelocX64.Unsigned => "X86_64_RELOC_UNSIGNED",
        MachRelocX64.Signed => "X86_64_RELOC_SIGNED",
        MachRelocX64.Branch => "X86_64_RELOC_BRANCH",
        MachRelocX64.GotLoad => "X86_64_RELOC_GOT_LOAD",
        MachRelocX64.Got => "X86_64_RELOC_GOT",
        MachRelocX64.Subtractor => "X86_64_RELOC_SUBTRACTOR",
        MachRelocX64.Signed1 => "X86_64_RELOC_SIGNED_1",
        MachRelocX64.Signed2 => "X86_64_RELOC_SIGNED_2",
        MachRelocX64.Signed4 => "X86_64_RELOC_SIGNED_4",
        MachRelocX64.Tlv => "X86_64_RELOC_TLV",
        _ => $"X86_64_RELOC_{type}",
    };

    public override bool IsUnsignedRelocation(byte type) => type == MachRelocX64.Unsigned;

    public override bool IsSubtractorRelocation(byte type) => type == MachRelocX64.Subtractor;

    public override bool IsGotPointerRelocation(byte type) => type == MachRelocX64.Got;

    public override List<MachRelocation> Decode(MachObjectFile file, MachInputSection section)
    {
        var raw = section.RawRelocations;
        var content = section.Content.Span;
        var result = new List<MachRelocation>(raw.Length);
        MachRawRelocation? subtractor = null;

        for (var i = 0; i < raw.Length; i++)
        {
            ref readonly var relocation = ref raw[i];
            if (relocation.Type == MachRelocX64.Subtractor)
            {
                subtractor = relocation;
                continue;
            }

            if (relocation.Type == MachRelocX64.Unsigned)
            {
                result.Add(DecodePointer(this, file, section, relocation, subtractor));
                subtractor = null;
                continue;
            }

            // Everything else is a 32-bit displacement relative to the end of the field.
            if (!relocation.PcRel || relocation.Length != 2 || relocation.Address < 0 || relocation.Address + 4 > content.Length)
            {
                throw Unsupported(this, section, relocation, "expected a 32-bit PC-relative field");
            }

            var offset = (uint)relocation.Address;
            var stored = LittleEndian.I32(content, relocation.Address);
            switch (relocation.Type)
            {
                case MachRelocX64.Signed or MachRelocX64.Signed1 or MachRelocX64.Signed2 or MachRelocX64.Signed4 or MachRelocX64.Branch:
                {
                    var kind = relocation.Type == MachRelocX64.Branch ? MachRelocKind.Branch : MachRelocKind.PcRel32;
                    if (relocation.IsExtern)
                    {
                        result.Add(new MachRelocation(offset, kind, 2, ExternSymbol(this, file, section, relocation), stored));
                        break;
                    }

                    // Without a symbol the stored value is the distance to the target inside the object file. The
                    // variants with a number say how many bytes of the instruction follow the field.
                    var trailing = relocation.Type switch
                    {
                        MachRelocX64.Signed1 => 1,
                        MachRelocX64.Signed2 => 2,
                        MachRelocX64.Signed4 => 4,
                        _ => 0,
                    };
                    var address = section.Address + offset + 4 + (ulong)trailing + (ulong)stored;
                    var (anchor, addend) = AnchorAt(this, file, section, relocation, address);
                    result.Add(new MachRelocation(offset, kind, 2, anchor, addend - trailing));
                    break;
                }

                case MachRelocX64.GotLoad:
                    result.Add(new MachRelocation(offset, MachRelocKind.GotLoad, 2, ExternSymbol(this, file, section, relocation), stored));
                    break;

                case MachRelocX64.Got:
                    result.Add(new MachRelocation(offset, MachRelocKind.Got, 2, ExternSymbol(this, file, section, relocation), stored));
                    break;

                case MachRelocX64.Tlv:
                    result.Add(new MachRelocation(offset, MachRelocKind.TlvLoad, 2, ExternSymbol(this, file, section, relocation), stored));
                    break;

                default:
                    throw Unsupported(this, section, relocation, "unknown relocation type");
            }

            subtractor = null;
        }

        return result;
    }

    public override void Apply(MachLinkContext context, Atom atom, Span<byte> data, in MachRelocation relocation)
    {
        if (relocation.Kind == MachRelocKind.Pointer)
        {
            ApplyPointer(context, atom, data, relocation);
            return;
        }

        var symbol = atom.File.Symbols[relocation.SymbolIndex];
        var offset = (int)relocation.Offset;

        // The displacement is relative to the address that follows the field.
        var next = atom.Address + relocation.Offset + 4;
        ulong target;
        switch (relocation.Kind)
        {
            case MachRelocKind.Branch:
                target = symbol.IsImported ? context.StubAddress(symbol) : symbol.Address;
                break;

            case MachRelocKind.PcRel32:
                target = symbol.Address;
                break;

            case MachRelocKind.GotLoad:
                if (NeedsGotEntry(symbol))
                {
                    target = context.GotAddress(symbol);
                }
                else
                {
                    LoadToLea(atom, relocation, symbol, data, offset);
                    target = symbol.Address;
                }

                break;

            case MachRelocKind.Got:
                target = context.GotAddress(symbol);
                break;

            // The descriptor of a thread-local variable of the image is addressed directly.
            case MachRelocKind.TlvLoad:
                LoadToLea(atom, relocation, symbol, data, offset);
                target = symbol.Address;
                break;

            default:
                throw new LinkerException($"{atom.DisplayName}: relocation {relocation.Kind} is not valid for x86_64");
        }

        var displacement = (long)(target - next) + relocation.Addend;
        if (displacement < int.MinValue || displacement > int.MaxValue)
        {
            throw OutOfRange(atom, relocation, symbol);
        }

        LittleEndian.I32(data, offset, (int)displacement);
    }

    public override void WriteStub(Span<byte> stub, ulong stubAddress, ulong slotAddress)
    {
        stub[0] = 0xff; // jmp *slot(%rip)
        stub[1] = 0x25;
        LittleEndian.I32(stub, 2, checked((int)(long)(slotAddress - (stubAddress + 6))));
    }

    public override ulong FunctionAlignment => 16;

    public override MachSyntheticFunction CreateObjCStub() => new(
        [
            0x48, 0x8b, 0x35, 0x00, 0x00, 0x00, 0x00,  // mov rsi, [rip + selector]
            0xff, 0x25, 0x00, 0x00, 0x00, 0x00,        // jmp [rip + _objc_msgSend@GOT]
        ],
        [
            new MachSyntheticRelocation(3, MachRelocKind.PcRel32, SelectorReference),
            new MachSyntheticRelocation(9, MachRelocKind.Got, ObjCMessageSend),
        ]);

    public override MachSyntheticFunction? CreateSupportFunction(string name) => name switch
    {
        // int32_t __isPlatformVersionAtLeast(uint32_t platform, uint32_t major, uint32_t minor, uint32_t subminor):
        // what "if (@available(macOS 13, *))" compiles to. The C library answers the question for a list of
        // {platform, version} pairs, with the version packed as xxxx.yy.zz.
        PlatformVersionCheck => new(
            [
                0x55,                          // push  rbp
                0x48, 0x89, 0xe5,              // mov   rbp, rsp
                0x48, 0x83, 0xec, 0x10,        // sub   rsp, 16
                0xc1, 0xe6, 0x10,              // shl   esi, 16
                0x0f, 0xb6, 0xd2,              // movzx edx, dl
                0xc1, 0xe2, 0x08,              // shl   edx, 8
                0x09, 0xd6,                    // or    esi, edx
                0x0f, 0xb6, 0xc9,              // movzx ecx, cl
                0x09, 0xce,                    // or    esi, ecx
                0x89, 0x7d, 0xf8,              // mov   [rbp - 8], edi
                0x89, 0x75, 0xfc,              // mov   [rbp - 4], esi
                0xbf, 0x01, 0x00, 0x00, 0x00,  // mov   edi, 1
                0x48, 0x8d, 0x75, 0xf8,        // lea   rsi, [rbp - 8]
                0xe8, 0x00, 0x00, 0x00, 0x00,  // call  __availability_version_check
                0x0f, 0xb6, 0xc0,              // movzx eax, al
                0x48, 0x83, 0xc4, 0x10,        // add   rsp, 16
                0x5d,                          // pop   rbp
                0xc3,                          // ret
            ],
            [new MachSyntheticRelocation(40, MachRelocKind.Branch, AvailabilityVersionCheck)],
            UnwindEncoding: 0x01000000), // a standard frame: the unwinder follows the frame pointer

        // int32_t __isOSVersionAtLeast(int32_t major, int32_t minor, int32_t subminor): the older form of the
        // check, which implies the platform (1 is macOS).
        OSVersionCheck => new(
            [
                0x89, 0xd1,                    // mov ecx, edx
                0x89, 0xf2,                    // mov edx, esi
                0x89, 0xfe,                    // mov esi, edi
                0xbf, 0x01, 0x00, 0x00, 0x00,  // mov edi, 1
                0xe9, 0x00, 0x00, 0x00, 0x00,  // jmp ___isPlatformVersionAtLeast
            ],
            [new MachSyntheticRelocation(12, MachRelocKind.Branch, PlatformVersionCheck)]),

        _ => null,
    };

    /// <summary>Turns <c>mov reg, [rip + got]</c> into <c>lea reg, [rip + symbol]</c>, which yields the address itself.</summary>
    private static void LoadToLea(Atom atom, in MachRelocation relocation, MachSymbol symbol, Span<byte> data, int offset)
    {
        if (offset < 2 || data[offset - 2] != MovLoad)
        {
            throw new LinkerException(
                $"{atom.DisplayName}+0x{atom.InputOffset + relocation.Offset:x}: the reference to '{symbol.Name}' is not a 64-bit load and cannot be rewritten");
        }

        data[offset - 2] = Lea;
    }
}
