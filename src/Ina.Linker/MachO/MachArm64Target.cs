
namespace Ina.Linker.MachO;

/// <summary>AArch64 (Apple silicon): relocation decoding and instruction patching.</summary>
internal sealed class MachArm64Target : MachTarget
{
    private const uint LoadX = 0xf9400000;          // ldr xN, [xM, #imm]
    private const uint LoadXMask = 0xffc00000;
    private const uint AddX = 0x91000000;           // add xN, xM, #imm

    public override uint CpuType => MachCpu.Arm64;

    public override uint CpuSubtype => MachCpu.SubtypeArm64All;

    public override string Name => "arm64";

    // The kernel of Apple silicon Macs uses 16 KiB pages.
    public override ulong PageSize => 0x4000;

    public override int StubSize => 12;

    public override ulong StubAlignment => 4;

    public override uint DwarfUnwindMode => CompactUnwind.ModeDwarfArm64;

    public override string RelocationName(byte type) => type switch
    {
        MachRelocArm64.Unsigned => "ARM64_RELOC_UNSIGNED",
        MachRelocArm64.Subtractor => "ARM64_RELOC_SUBTRACTOR",
        MachRelocArm64.Branch26 => "ARM64_RELOC_BRANCH26",
        MachRelocArm64.Page21 => "ARM64_RELOC_PAGE21",
        MachRelocArm64.PageOff12 => "ARM64_RELOC_PAGEOFF12",
        MachRelocArm64.GotLoadPage21 => "ARM64_RELOC_GOT_LOAD_PAGE21",
        MachRelocArm64.GotLoadPageOff12 => "ARM64_RELOC_GOT_LOAD_PAGEOFF12",
        MachRelocArm64.PointerToGot => "ARM64_RELOC_POINTER_TO_GOT",
        MachRelocArm64.TlvpLoadPage21 => "ARM64_RELOC_TLVP_LOAD_PAGE21",
        MachRelocArm64.TlvpLoadPageOff12 => "ARM64_RELOC_TLVP_LOAD_PAGEOFF12",
        MachRelocArm64.Addend => "ARM64_RELOC_ADDEND",
        _ => $"ARM64_RELOC_{type}",
    };

    public override bool IsUnsignedRelocation(byte type) => type == MachRelocArm64.Unsigned;

    public override bool IsSubtractorRelocation(byte type) => type == MachRelocArm64.Subtractor;

    public override bool IsGotPointerRelocation(byte type) => type == MachRelocArm64.PointerToGot;

    public override List<MachRelocation> Decode(MachObjectFile file, MachInputSection section)
    {
        var raw = section.RawRelocations;
        var result = new List<MachRelocation>(raw.Length);
        long addend = 0;
        MachRawRelocation? subtractor = null;

        for (var i = 0; i < raw.Length; i++)
        {
            ref readonly var relocation = ref raw[i];
            switch (relocation.Type)
            {
                case MachRelocArm64.Addend:
                    // The addend of the entry that follows, stored in the field that otherwise names the symbol.
                    addend = (relocation.SymbolNumber << 8) >> 8;
                    continue;

                case MachRelocArm64.Subtractor:
                    subtractor = relocation;
                    continue;

                case MachRelocArm64.Unsigned:
                    result.Add(DecodePointer(this, file, section, relocation, subtractor));
                    break;

                case MachRelocArm64.PointerToGot:
                    if (!relocation.PcRel || relocation.Length != 2 || subtractor is not null)
                    {
                        throw Unsupported(this, section, relocation, "expected a 32-bit PC-relative field");
                    }

                    // Unlike for other data relocations, the content of the field is not an addend: assemblers leave
                    // a meaningless intermediate value there, and Apple's linker ignores it.
                    result.Add(new MachRelocation((uint)relocation.Address, MachRelocKind.PointerToGot, 2, ExternSymbol(this, file, section, relocation), 0));
                    break;

                default:
                    var kind = relocation.Type switch
                    {
                        MachRelocArm64.Branch26 => MachRelocKind.Branch,
                        MachRelocArm64.Page21 => MachRelocKind.Page21,
                        MachRelocArm64.PageOff12 => MachRelocKind.PageOff12,
                        MachRelocArm64.GotLoadPage21 => MachRelocKind.GotLoadPage21,
                        MachRelocArm64.GotLoadPageOff12 => MachRelocKind.GotLoadPageOff12,
                        MachRelocArm64.TlvpLoadPage21 => MachRelocKind.TlvLoadPage21,
                        MachRelocArm64.TlvpLoadPageOff12 => MachRelocKind.TlvLoadPageOff12,
                        _ => throw Unsupported(this, section, relocation, "unknown relocation type"),
                    };
                    if (relocation.Length != 2 || relocation.Address < 0 || relocation.Address + 4 > section.Content.Length || subtractor is not null)
                    {
                        throw Unsupported(this, section, relocation, "expected an instruction");
                    }

                    result.Add(new MachRelocation((uint)relocation.Address, kind, 2, ExternSymbol(this, file, section, relocation), addend));
                    break;
            }

            addend = 0;
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
        var place = atom.Address + relocation.Offset;
        var field = data[(int)relocation.Offset..];
        var instruction = LittleEndian.U32(field, 0);

        switch (relocation.Kind)
        {
            case MachRelocKind.Branch:
            {
                var target = symbol.IsImported ? context.StubAddress(symbol) : symbol.Address + (ulong)relocation.Addend;
                var distance = (long)(target - place);
                if ((distance & 3) != 0 || distance < -(1L << 27) || distance >= 1L << 27)
                {
                    throw OutOfRange(atom, relocation, symbol);
                }

                LittleEndian.U32(field, 0, (instruction & 0xfc000000) | ((uint)(distance >> 2) & 0x03ffffff));
                break;
            }

            case MachRelocKind.Page21:
                LittleEndian.U32(field, 0, EncodePage(atom, relocation, symbol, instruction, symbol.Address + (ulong)relocation.Addend, place));
                break;

            case MachRelocKind.PageOff12:
                LittleEndian.U32(field, 0, EncodePageOffset(atom, relocation, symbol, instruction, symbol.Address + (ulong)relocation.Addend));
                break;

            case MachRelocKind.GotLoadPage21:
            {
                var target = NeedsGotEntry(symbol) ? context.GotAddress(symbol) : symbol.Address;
                LittleEndian.U32(field, 0, EncodePage(atom, relocation, symbol, instruction, target + (ulong)relocation.Addend, place));
                break;
            }

            case MachRelocKind.GotLoadPageOff12:
                if (NeedsGotEntry(symbol))
                {
                    LittleEndian.U32(field, 0, EncodePageOffset(atom, relocation, symbol, instruction, context.GotAddress(symbol) + (ulong)relocation.Addend));
                }
                else
                {
                    LittleEndian.U32(field, 0, LoadToAdd(atom, relocation, symbol, instruction, symbol.Address + (ulong)relocation.Addend));
                }

                break;

            // The descriptor of a thread-local variable of the image is addressed directly, like a relaxed GOT load.
            case MachRelocKind.TlvLoadPage21:
                LittleEndian.U32(field, 0, EncodePage(atom, relocation, symbol, instruction, symbol.Address + (ulong)relocation.Addend, place));
                break;

            case MachRelocKind.TlvLoadPageOff12:
                LittleEndian.U32(field, 0, LoadToAdd(atom, relocation, symbol, instruction, symbol.Address + (ulong)relocation.Addend));
                break;

            case MachRelocKind.PointerToGot:
            {
                var distance = (long)(context.GotAddress(symbol) - place) + relocation.Addend;
                if (distance < int.MinValue || distance > int.MaxValue)
                {
                    throw OutOfRange(atom, relocation, symbol);
                }

                LittleEndian.I32(field, 0, (int)distance);
                break;
            }

            default:
                throw new LinkerException($"{atom.DisplayName}: relocation {relocation.Kind} is not valid for arm64");
        }
    }

    public override void WriteStub(Span<byte> stub, ulong stubAddress, ulong slotAddress)
    {
        var pages = (long)((slotAddress & ~0xfffUL) - (stubAddress & ~0xfffUL)) >> 12;
        LittleEndian.U32(stub, 0, 0x90000010 | (((uint)pages & 3) << 29) | ((((uint)(pages >> 2)) & 0x7ffff) << 5)); // adrp x16, slot@PAGE
        LittleEndian.U32(stub, 4, 0xf9400210 | ((uint)((slotAddress & 0xfff) >> 3) << 10));                          // ldr  x16, [x16, slot@PAGEOFF]
        LittleEndian.U32(stub, 8, 0xd61f0200);                                                                       // br   x16
    }

    public override ulong FunctionAlignment => 4;

    public override MachSyntheticFunction CreateObjCStub() => new(
        Words(
            0x90000001,  // adrp x1, selector@PAGE
            0xf9400021,  // ldr  x1, [x1, selector@PAGEOFF]
            0x90000010,  // adrp x16, _objc_msgSend@GOTPAGE
            0xf9400210,  // ldr  x16, [x16, _objc_msgSend@GOTPAGEOFF]
            0xd61f0200), // br   x16
        [
            new MachSyntheticRelocation(0, MachRelocKind.Page21, SelectorReference),
            new MachSyntheticRelocation(4, MachRelocKind.PageOff12, SelectorReference),
            new MachSyntheticRelocation(8, MachRelocKind.GotLoadPage21, ObjCMessageSend),
            new MachSyntheticRelocation(12, MachRelocKind.GotLoadPageOff12, ObjCMessageSend),
        ]);

    public override MachSyntheticFunction? CreateSupportFunction(string name) => name switch
    {
        // int32_t __isPlatformVersionAtLeast(uint32_t platform, uint32_t major, uint32_t minor, uint32_t subminor):
        // what "if (@available(macOS 13, *))" compiles to. The C library answers the question for a list of
        // {platform, version} pairs, with the version packed as xxxx.yy.zz.
        PlatformVersionCheck => new(
            Words(
                0xd10083ff,  // sub   sp, sp, #32
                0xa9017bfd,  // stp   x29, x30, [sp, #16]
                0x910043fd,  // add   x29, sp, #16
                0x53103c28,  // lsl   w8, w1, #16
                0x33181c48,  // bfi   w8, w2, #8, #8
                0x33001c68,  // bfxil w8, w3, #0, #8
                0x290123e0,  // stp   w0, w8, [sp, #8]
                0x52800020,  // mov   w0, #1
                0x910023e1,  // add   x1, sp, #8
                0x94000000,  // bl    __availability_version_check
                0x12000000,  // and   w0, w0, #1
                0xa9417bfd,  // ldp   x29, x30, [sp, #16]
                0x910083ff,  // add   sp, sp, #32
                0xd65f03c0), // ret
            [new MachSyntheticRelocation(36, MachRelocKind.Branch, AvailabilityVersionCheck)],
            UnwindEncoding: 0x04000000), // a standard frame: the unwinder follows the frame pointer

        // int32_t __isOSVersionAtLeast(int32_t major, int32_t minor, int32_t subminor): the older form of the
        // check, which implies the platform (1 is macOS).
        OSVersionCheck => new(
            Words(
                0x2a0203e3,  // mov w3, w2
                0x2a0103e2,  // mov w2, w1
                0x2a0003e1,  // mov w1, w0
                0x52800020,  // mov w0, #1
                0x14000000), // b   ___isPlatformVersionAtLeast
            [new MachSyntheticRelocation(16, MachRelocKind.Branch, PlatformVersionCheck)]),

        _ => null,
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

    private static uint EncodePage(Atom atom, in MachRelocation relocation, MachSymbol symbol, uint instruction, ulong target, ulong place)
    {
        var pages = (long)((target & ~0xfffUL) - (place & ~0xfffUL)) >> 12;
        if (pages < -(1L << 20) || pages >= 1L << 20)
        {
            throw OutOfRange(atom, relocation, symbol);
        }

        return (instruction & 0x9f00001f) | (((uint)pages & 3) << 29) | ((((uint)(pages >> 2)) & 0x7ffff) << 5);
    }

    private static uint EncodePageOffset(Atom atom, in MachRelocation relocation, MachSymbol symbol, uint instruction, ulong target)
    {
        var offset = (uint)(target & 0xfff);
        if ((instruction & 0x3b000000) == 0x39000000)
        {
            // Loads and stores scale the offset by the size of the access; 128-bit vector accesses use a scale of 16.
            var scale = (int)(instruction >> 30);
            if (scale == 0 && (instruction & 0x04800000) == 0x04800000)
            {
                scale = 4;
            }

            if ((offset & ((1u << scale) - 1)) != 0)
            {
                throw new LinkerException(
                    $"{atom.DisplayName}+0x{atom.InputOffset + relocation.Offset:x}: '{symbol.Name}' is not aligned for the instruction that accesses it");
            }

            offset >>= scale;
        }

        return (instruction & 0xffc003ff) | (offset << 10);
    }

    /// <summary>Turns <c>ldr xN, [xM, #got_offset]</c> into <c>add xN, xM, #offset</c>, which yields the address itself.</summary>
    private static uint LoadToAdd(Atom atom, in MachRelocation relocation, MachSymbol symbol, uint instruction, ulong target)
    {
        if ((instruction & LoadXMask) != LoadX)
        {
            throw new LinkerException(
                $"{atom.DisplayName}+0x{atom.InputOffset + relocation.Offset:x}: the reference to '{symbol.Name}' is not a 64-bit load and cannot be rewritten");
        }

        return AddX | ((uint)(target & 0xfff) << 10) | (instruction & 0x3ff);
    }
}
