namespace Ina.Linker.Tests.Support;

/// <summary>Raised when the emulated code executes a breakpoint instruction, which is how Windows code fails fast.</summary>
internal sealed class Arm64TrapException(uint code) : Exception($"brk #0x{code:x}")
{
    public uint Code { get; } = code;
}

/// <summary>
/// Interprets the small subset of AArch64 that the linker generates for Windows programs, so that this code can
/// be executed by the tests on any machine. It runs the linked image: what it executes went through the
/// relocations and the layout of the linker. Calls of imported functions are handed to the test.
/// </summary>
internal sealed class Arm64Emulator
{
    private const ulong ReturnSentinel = 0xdead_0000;
    private const int PageSize = 0x1000;

    private readonly PeImage _image;
    private readonly Dictionary<ulong, byte[]> _pages = [];
    private bool _negative, _zero, _carry, _overflow;
    private ulong _exclusiveAddress = ulong.MaxValue;

    public Arm64Emulator(PeImage image)
    {
        _image = image;
        foreach (var section in image.Sections)
        {
            Write(image.ImageBase + section.Rva, image.Read(section.Rva, (int)section.VirtualSize));
        }

        Sp = 0x7fff_0000;
    }

    /// <summary>The general purpose registers x0 to x30.</summary>
    public ulong[] X { get; } = new ulong[31];

    public ulong Sp { get; set; }

    public ulong Pc { get; private set; }

    /// <summary>Simulates an imported function; the result becomes its return value.</summary>
    public Func<string, Arm64Emulator, ulong> Import { get; set; } = static (name, _) => throw new InvalidOperationException($"unexpected call of {name}");

    /// <summary>The pages that were read or written, by their base address.</summary>
    public List<ulong> TouchedPages { get; } = [];

    public ulong AddressOf(uint rva) => _image.ImageBase + rva;

    /// <summary>Calls the function at <paramref name="address"/> with integer arguments and returns x0.</summary>
    public ulong Call(ulong address, params ulong[] arguments)
    {
        arguments.CopyTo(X, 0);
        var (savedPc, savedLink) = (Pc, X[30]);
        X[30] = ReturnSentinel;
        Pc = address;
        for (var steps = 0; Pc != ReturnSentinel; steps++)
        {
            Assert.True(steps < 1_000_000, "the emulated code does not terminate");
            if (_image.ImportNames.TryGetValue(Pc, out var import))
            {
                // The thunk jumped to what the address slot holds in the file: stand in for the function.
                var link = X[30];
                X[0] = Import(import, this);
                Pc = link;
                continue;
            }

            Step();
        }

        (Pc, X[30]) = (savedPc, savedLink);
        return X[0];
    }

    public byte[] Read(ulong address, int length)
    {
        var result = new byte[length];
        for (var i = 0; i < length; i++)
        {
            result[i] = Page(address + (ulong)i)[(address + (ulong)i) % PageSize];
        }

        return result;
    }

    public void Write(ulong address, ReadOnlySpan<byte> data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            Page(address + (ulong)i)[(address + (ulong)i) % PageSize] = data[i];
        }
    }

    public ulong Read64(ulong address) => BitConverter.ToUInt64(Read(address, 8));

    public uint Read32(ulong address) => BitConverter.ToUInt32(Read(address, 4));

    public void Write64(ulong address, ulong value) => Write(address, BitConverter.GetBytes(value));

    public void Write32(ulong address, uint value) => Write(address, BitConverter.GetBytes(value));

    private byte[] Page(ulong address)
    {
        var page = address / PageSize * PageSize;
        if (!_pages.TryGetValue(page, out var content))
        {
            _pages[page] = content = new byte[PageSize];
        }

        if (TouchedPages.Count == 0 || TouchedPages[^1] != page)
        {
            TouchedPages.Add(page);
        }

        return content;
    }

    /// <summary>Register 31 is the stack pointer or the zero register, depending on the instruction.</summary>
    private ulong Register(int number, bool stackPointer = false) => number == 31 ? (stackPointer ? Sp : 0) : X[number];

    private void SetRegister(int number, ulong value, bool is64 = true, bool stackPointer = false)
    {
        value = is64 ? value : (uint)value;
        if (number != 31)
        {
            X[number] = value;
        }
        else if (stackPointer)
        {
            Sp = value;
        }
    }

    private static long SignExtend(uint value, int bits) => (long)((ulong)value << (64 - bits)) >> (64 - bits);

    private ulong AddWithFlags(ulong left, ulong right, bool subtract, bool is64, bool setFlags)
    {
        // Subtraction is the addition of the complement plus one, which is also how the carry flag is defined.
        var operand = subtract ? ~right : right;
        var carryIn = subtract ? 1u : 0u;
        ulong result;
        bool carry, overflow;
        if (is64)
        {
            var wide = (UInt128)left + operand + carryIn;
            result = (ulong)wide;
            carry = wide > ulong.MaxValue;
            overflow = ((left ^ result) & (operand ^ result) & (1ul << 63)) != 0;
        }
        else
        {
            var wide = (ulong)(uint)left + (uint)operand + carryIn;
            result = (uint)wide;
            carry = wide > uint.MaxValue;
            overflow = (((uint)left ^ (uint)result) & ((uint)operand ^ (uint)result) & 0x8000_0000) != 0;
        }

        if (setFlags)
        {
            _negative = (result >> (is64 ? 63 : 31) & 1) != 0;
            _zero = result == 0;
            _carry = carry;
            _overflow = overflow;
        }

        return result;
    }

    private bool Condition(uint condition) => (condition >> 1) switch
    {
        0 => _zero,
        1 => _carry,
        2 => _negative,
        3 => _overflow,
        4 => _carry && !_zero,
        5 => _negative == _overflow,
        6 => !_zero && _negative == _overflow,
        _ => true,
    } != ((condition & 1) != 0 && condition != 15);

    private void Step()
    {
        var instruction = Read32(Pc);
        var is64 = instruction >> 31 != 0;
        var rd = (int)(instruction & 31);
        var rn = (int)((instruction >> 5) & 31);
        var rm = (int)((instruction >> 16) & 31);
        var next = Pc + 4;

        if ((instruction & 0x7f80_0000) == 0x5280_0000)
        {
            // movz
            SetRegister(rd, (ulong)((instruction >> 5) & 0xffff) << (int)(((instruction >> 21) & 3) * 16), is64);
        }
        else if ((instruction & 0x1f00_0000) == 0x1100_0000)
        {
            // add/sub (immediate), optionally setting flags; register 31 is the stack pointer except as a flag-setting destination
            var setFlags = (instruction & 0x2000_0000) != 0;
            var immediate = (ulong)((instruction >> 10) & 0xfff) << ((instruction & 0x0040_0000) != 0 ? 12 : 0);
            var result = AddWithFlags(Register(rn, stackPointer: true), immediate, (instruction & 0x4000_0000) != 0, is64, setFlags);
            SetRegister(rd, result, is64, stackPointer: !setFlags);
        }
        else if ((instruction & 0x1f20_0000) == 0x0b00_0000)
        {
            // add/sub (shifted register); only "lsl #0" is generated
            Assert.Equal(0u, (instruction >> 10) & 0x3f);
            var setFlags = (instruction & 0x2000_0000) != 0;
            SetRegister(rd, AddWithFlags(Register(rn), Register(rm), (instruction & 0x4000_0000) != 0, is64, setFlags), is64);
        }
        else if ((instruction & 0x1f20_0000) == 0x0a00_0000)
        {
            // and/orr/eor (shifted register); "mov rd, rm" is "orr rd, zr, rm"
            Assert.Equal(0u, (instruction >> 10) & 0x3f);
            var (left, right) = (Register(rn), Register(rm));
            SetRegister(rd, ((instruction >> 29) & 3) switch { 0 => left & right, 1 => left | right, 2 => left ^ right, _ => throw Unsupported(instruction) }, is64);
        }
        else if ((instruction & 0x7f80_0000) == 0x5300_0000)
        {
            // ubfm, in the form of "lsl rd, rn, #shift"
            var (rotate, size) = ((int)((instruction >> 16) & 0x3f), is64 ? 64 : 32);
            Assert.Equal((uint)(rotate - 1), (instruction >> 10) & 0x3f);
            SetRegister(rd, Register(rn) << (size - rotate), is64);
        }
        else if ((instruction & 0x7c00_0000) == 0x1400_0000)
        {
            // b, bl
            if (is64)
            {
                X[30] = next;
            }

            next = (ulong)((long)Pc + (SignExtend(instruction & 0x03ff_ffff, 26) << 2));
        }
        else if ((instruction & 0xff00_0010) == 0x5400_0000)
        {
            // b.cond
            if (Condition(instruction & 15))
            {
                next = (ulong)((long)Pc + (SignExtend((instruction >> 5) & 0x7ffff, 19) << 2));
            }
        }
        else if ((instruction & 0x7e00_0000) == 0x3400_0000)
        {
            // cbz, cbnz
            var value = is64 ? Register(rd) : (uint)Register(rd);
            if (value == 0 == ((instruction & 0x0100_0000) == 0))
            {
                next = (ulong)((long)Pc + (SignExtend((instruction >> 5) & 0x7ffff, 19) << 2));
            }
        }
        else if ((instruction & 0xff9f_fc1f) == 0xd61f_0000)
        {
            // br, blr, ret
            var target = Register(rn);
            if (((instruction >> 21) & 3) == 1)
            {
                X[30] = next;
            }

            next = target;
        }
        else if ((instruction & 0x1f00_0000) == 0x1000_0000)
        {
            // adr, adrp
            var immediate = SignExtend((instruction >> 29 & 3) | (instruction >> 3 & 0x1f_fffc), 21);
            SetRegister(rd, is64 ? (ulong)((long)(Pc & ~0xffful) + (immediate << 12)) : (ulong)((long)Pc + immediate));
        }
        else if ((instruction & 0x3b00_0000) == 0x3900_0000)
        {
            // ldr/str/ldrb/strb with an unsigned scaled offset
            var size = (int)(instruction >> 30);
            LoadOrStore(rd, Register(rn, stackPointer: true) + (((instruction >> 10) & 0xfff) << size), size, (instruction & 0x0040_0000) != 0);
        }
        else if ((instruction & 0x3b20_0c00) == 0x3820_0800)
        {
            // ldr/str with a register offset, optionally shifted by the access size
            var size = (int)(instruction >> 30);
            var index = Register(rm) << ((instruction & 0x1000) != 0 ? size : 0);
            LoadOrStore(rd, Register(rn, stackPointer: true) + index, size, (instruction & 0x0040_0000) != 0);
        }
        else if ((instruction & 0xffc0_0000) is 0xa880_0000 or 0xa980_0000 or 0xa8c0_0000 or 0xa9c0_0000)
        {
            // stp/ldp of 64-bit registers with post-index (a8) or pre-index (a9) writeback
            var offset = (ulong)(SignExtend((instruction >> 15) & 0x7f, 7) * 8);
            var preIndex = (instruction & 0x0100_0000) != 0;
            var address = Register(rn, stackPointer: true) + (preIndex ? offset : 0);
            var second = (int)((instruction >> 10) & 31);
            if ((instruction & 0x0040_0000) != 0)
            {
                SetRegister(rd, Read64(address));
                SetRegister(second, Read64(address + 8));
            }
            else
            {
                Write64(address, Register(rd));
                Write64(address + 8, Register(second));
            }

            SetRegister(rn, preIndex ? address : address + offset, stackPointer: true);
        }
        else if ((instruction & 0xbfff_fc00) == 0x885f_fc00)
        {
            // ldaxr
            _exclusiveAddress = Register(rn, stackPointer: true);
            SetRegister(rd, instruction >> 30 == 3 ? Read64(_exclusiveAddress) : Read32(_exclusiveAddress));
        }
        else if ((instruction & 0xbfe0_fc00) == 0x8800_fc00)
        {
            // stlxr: succeeds because nothing else runs between the load and the store
            var address = Register(rn, stackPointer: true);
            Assert.Equal(_exclusiveAddress, address);
            LoadOrStore(rd, address, (int)(instruction >> 30), isLoad: false);
            SetRegister(rm, 0);
            _exclusiveAddress = ulong.MaxValue;
        }
        else if (instruction == 0xd503_3bbf)
        {
            // dmb ish
        }
        else if ((instruction & 0xffe0_001f) == 0xd420_0000)
        {
            throw new Arm64TrapException((instruction >> 5) & 0xffff);
        }
        else
        {
            throw Unsupported(instruction);
        }

        Pc = next;
    }

    private void LoadOrStore(int register, ulong address, int size, bool isLoad)
    {
        var length = 1 << size;
        if (isLoad)
        {
            var bytes = new byte[8];
            Read(address, length).CopyTo(bytes, 0);
            SetRegister(register, BitConverter.ToUInt64(bytes));
        }
        else
        {
            Write(address, BitConverter.GetBytes(Register(register)).AsSpan(0, length));
        }
    }

    private InvalidOperationException Unsupported(uint instruction) =>
        new($"the emulator does not know the instruction 0x{instruction:x8} at 0x{Pc:x}");
}
