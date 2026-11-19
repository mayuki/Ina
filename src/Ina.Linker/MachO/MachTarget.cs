using Ina.Linker.Elf;

namespace Ina.Linker.MachO;

/// <summary>A relocation in linker-generated code, expressed against a symbol name.</summary>
internal readonly record struct MachSyntheticRelocation(uint Offset, MachRelocKind Kind, string Symbol);

/// <summary>
/// A function whose machine code the linker generates itself. These stand in for what Apple's toolchain adds to a
/// link on its own: selector stubs for Objective-C calls and the support library of the compiler.
/// </summary>
/// <param name="UnwindEncoding">The compact unwind encoding of the function; 0 when it has no frame worth describing.</param>
internal sealed record MachSyntheticFunction(byte[] Code, MachSyntheticRelocation[] Relocations, uint UnwindEncoding = 0);

/// <summary>The architecture specific parts of the Mach-O linker.</summary>
internal abstract class MachTarget
{
    /// <summary>In the relocations of <see cref="CreateObjCStub"/>: the pointer to the selector that the stub loads.</summary>
    public const string SelectorReference = "<selector reference>";

    /// <summary>The Objective-C runtime function that sends a message.</summary>
    public const string ObjCMessageSend = "_objc_msgSend";

    protected const string PlatformVersionCheck = "___isPlatformVersionAtLeast";
    protected const string OSVersionCheck = "___isOSVersionAtLeast";

    /// <summary>The C library function that answers whether the running system is at least a given version.</summary>
    protected const string AvailabilityVersionCheck = "__availability_version_check";

    private static readonly MachArm64Target Arm64 = new();
    private static readonly MachX64Target X64 = new();

    public abstract uint CpuType { get; }

    public abstract uint CpuSubtype { get; }

    /// <summary>The subtype in the header of an executable, which may carry capability bits in addition.</summary>
    public virtual uint ExecutableCpuSubtype => CpuSubtype;

    public abstract string Name { get; }

    /// <summary>The page size of the platform; segments are aligned to it.</summary>
    public abstract ulong PageSize { get; }

    public abstract int StubSize { get; }

    public abstract ulong StubAlignment { get; }

    /// <summary>The compact unwind mode that sends the unwinder to an FDE in <c>__eh_frame</c>.</summary>
    public abstract uint DwarfUnwindMode { get; }

    public static MachTarget Create(TargetArchitecture architecture) => architecture switch
    {
        TargetArchitecture.Arm64 => Arm64,
        TargetArchitecture.X64 => X64,
        _ => throw new LinkerException($"unsupported target architecture: {architecture}"),
    };

    public static MachTarget ForCpu(uint cpuType, string fileName) => cpuType switch
    {
        MachCpu.Arm64 => Arm64,
        MachCpu.X64 => X64,
        _ => throw new LinkerException($"{fileName}: unsupported processor type 0x{cpuType:x}"),
    };

    public abstract string RelocationName(byte type);

    public abstract bool IsUnsignedRelocation(byte type);

    public abstract bool IsSubtractorRelocation(byte type);

    /// <summary>Whether a relocation type yields the distance to the GOT entry of a symbol in a data field.</summary>
    public abstract bool IsGotPointerRelocation(byte type);

    /// <summary>Decodes the relocation entries of a regular section into self-contained records.</summary>
    public abstract List<MachRelocation> Decode(MachObjectFile file, MachInputSection section);

    /// <summary>Patches <paramref name="data"/> (the content of <paramref name="atom"/>) for one relocation.</summary>
    public abstract void Apply(MachLinkContext context, Atom atom, Span<byte> data, in MachRelocation relocation);

    /// <summary>Writes a stub that jumps to the address stored in a GOT entry.</summary>
    public abstract void WriteStub(Span<byte> stub, ulong stubAddress, ulong slotAddress);

    /// <summary>The alignment of linker-generated functions.</summary>
    public abstract ulong FunctionAlignment { get; }

    /// <summary>
    /// Creates the stub behind a call to <c>_objc_msgSend$selector</c>: it loads the selector into the second
    /// argument register and jumps to <c>objc_msgSend</c>. Compilers leave these stubs to the linker so that all
    /// calls with the same selector share one.
    /// </summary>
    public abstract MachSyntheticFunction CreateObjCStub();

    /// <summary>
    /// Creates a function of the compiler support library (compiler-rt) that object files may refer to, or returns
    /// null when <paramref name="name"/> is not one the linker provides. Apple's toolchain links that library
    /// statically into every program; Ina generates the few functions that matter instead.
    /// </summary>
    public abstract MachSyntheticFunction? CreateSupportFunction(string name);

    /// <summary>
    /// Whether references that load the address of a symbol from the GOT really need a GOT entry. Symbols of the
    /// image itself have a fixed distance from the code, so such loads are rewritten to compute the address.
    /// </summary>
    public static bool NeedsGotEntry(MachSymbol symbol) => !symbol.IsImageRelative;

    /// <summary>Decides which GOT entries, stubs and load-time fixups a relocation requires.</summary>
    public void Scan(MachLinkContext context, Atom atom, in MachRelocation relocation)
    {
        var symbol = atom.File.Symbols[relocation.SymbolIndex];
        switch (relocation.Kind)
        {
            case MachRelocKind.Branch:
                if (symbol.IsImported)
                {
                    context.AddStub(symbol);
                }
                else if (symbol.Kind == MachSymbolKind.Undefined && !symbol.HasStrongReference)
                {
                    // An undefined symbol with a strong reference has already been reported as such.
                    throw new LinkerException($"{atom.DisplayName}: call to '{symbol.Name}', which is only declared weakly and not available");
                }

                break;

            case MachRelocKind.GotLoadPage21 or MachRelocKind.GotLoadPageOff12 or MachRelocKind.GotLoad:
                if (NeedsGotEntry(symbol))
                {
                    context.AddGotEntry(symbol);
                }

                break;

            case MachRelocKind.PointerToGot or MachRelocKind.Got:
                context.AddGotEntry(symbol);
                break;

            case MachRelocKind.TlvLoadPage21 or MachRelocKind.TlvLoadPageOff12 or MachRelocKind.TlvLoad:
                if (!symbol.IsThreadLocal)
                {
                    // A variable of a dynamic library would need an entry in __thread_ptrs, which nothing in a
                    // NativeAOT link requires.
                    throw new LinkerException($"{atom.DisplayName}: thread-local access to '{symbol.Name}', which is not a thread-local variable of this image");
                }

                break;

            case MachRelocKind.Pointer:
                ScanPointer(context, atom, relocation, symbol);
                break;

            case MachRelocKind.Page21 or MachRelocKind.PageOff12 or MachRelocKind.PcRel32:
                if (symbol.IsImported)
                {
                    throw new LinkerException(
                        $"{atom.DisplayName}: direct reference to '{symbol.Name}', which is defined in {symbol.Import!.Library}; the code must go through the GOT");
                }

                break;
        }
    }

    protected static LinkerException Unsupported(MachTarget target, MachInputSection section, in MachRawRelocation relocation, string reason) =>
        new($"{section.DisplayName}+0x{relocation.Address:x}: unsupported relocation {target.RelocationName(relocation.Type)}: {reason}");

    protected static LinkerException OutOfRange(Atom atom, in MachRelocation relocation, MachSymbol symbol) =>
        new($"{atom.DisplayName}+0x{atom.InputOffset + relocation.Offset:x}: relocation {relocation.Kind} against '{symbol.Name}' is out of range");

    /// <summary>The value of a relocation that stores an address or a difference of addresses.</summary>
    protected static void ApplyPointer(MachLinkContext context, Atom atom, Span<byte> data, in MachRelocation relocation)
    {
        var symbol = atom.File.Symbols[relocation.SymbolIndex];
        long value;
        if (relocation.SubtrahendIndex >= 0)
        {
            value = (long)symbol.Address + relocation.Addend - (long)atom.File.Symbols[relocation.SubtrahendIndex].Address;
        }
        else if (symbol.IsImported)
        {
            // The loader stores the address; the addend is part of the bind instruction.
            value = 0;
        }
        else if (symbol.Kind == MachSymbolKind.Undefined)
        {
            value = 0;
        }
        else if (IsThreadLocalOffset(atom, symbol))
        {
            value = (long)(symbol.Address - context.ThreadLocalTemplateStart) + relocation.Addend;
        }
        else
        {
            value = (long)symbol.Address + relocation.Addend;
        }

        var field = data[(int)relocation.Offset..];
        if (relocation.Length == 3)
        {
            LittleEndian.I64(field, 0, value);
        }
        else
        {
            if (value < int.MinValue || value > uint.MaxValue)
            {
                throw OutOfRange(atom, relocation, symbol);
            }

            LittleEndian.U32(field, 0, (uint)value);
        }
    }

    /// <summary>
    /// Whether a pointer is the third word of a thread-local variable descriptor. It refers to the initial value
    /// of the variable, and the loader expects its offset inside the per-thread block rather than an address.
    /// </summary>
    private static bool IsThreadLocalOffset(Atom atom, MachSymbol symbol) =>
        atom.Section.Type == MachSectionType.ThreadLocalVariables
        && symbol.Atom?.Section.Type is MachSectionType.ThreadLocalRegular or MachSectionType.ThreadLocalZeroFill;

    /// <summary>Resolves a relocation that refers to a section by address to an anchor symbol and an offset.</summary>
    protected static (int SymbolIndex, long Addend) AnchorAt(
        MachTarget target, MachObjectFile file, MachInputSection section, in MachRawRelocation relocation, ulong address)
    {
        var atom = file.FindAtom(address, relocation.SymbolNumber)
            ?? throw Unsupported(target, section, relocation, $"the address 0x{address:x} is not inside a section of the object file");
        return (file.AnchorFor(atom), (long)(address - atom.InputAddress));
    }

    protected static int ExternSymbol(MachTarget target, MachObjectFile file, MachInputSection section, in MachRawRelocation relocation)
    {
        if (!relocation.IsExtern)
        {
            throw Unsupported(target, section, relocation, "expected a reference to a symbol");
        }

        if (relocation.SymbolNumber >= file.Nlist.Count)
        {
            throw Unsupported(target, section, relocation, "symbol index out of range");
        }

        return relocation.SymbolNumber;
    }

    /// <summary>Decodes a pointer or, together with the preceding subtractor entry, a difference of two symbols.</summary>
    protected static MachRelocation DecodePointer(
        MachTarget target, MachObjectFile file, MachInputSection section, in MachRawRelocation relocation, MachRawRelocation? subtractor)
    {
        if (relocation.PcRel || relocation.Length < 2)
        {
            throw Unsupported(target, section, relocation, "expected a 32-bit or 64-bit absolute field");
        }

        var content = section.Content.Span;
        var size = 1 << relocation.Length;
        if (relocation.Address < 0 || relocation.Address + size > content.Length)
        {
            throw Unsupported(target, section, relocation, "the field is outside of the section");
        }

        var value = relocation.Length == 3 ? LittleEndian.I64(content, relocation.Address) : LittleEndian.I32(content, relocation.Address);
        if (subtractor is { } sub)
        {
            if (sub.Length != relocation.Length)
            {
                throw Unsupported(target, section, sub, "both operands of a difference must have the same size");
            }

            if (sub.IsExtern && relocation.IsExtern)
            {
                return new MachRelocation(
                    (uint)relocation.Address, MachRelocKind.Pointer, relocation.Length, ExternSymbol(target, file, section, relocation), value,
                    ExternSymbol(target, file, section, sub));
            }

            // x86-64 assemblers refer to labels without a symbol by section. The address of such a label inside
            // the object file is then part of the content.
            if (sub.IsExtern)
            {
                // The minuend is the label: the content is its address.
                var (minuend, offset) = AnchorAt(target, file, section, relocation, relocation.Length == 3 ? (ulong)value : (uint)value);
                return new MachRelocation(
                    (uint)relocation.Address, MachRelocKind.Pointer, relocation.Length, minuend, offset, ExternSymbol(target, file, section, sub));
            }

            if (relocation.IsExtern && sub.SymbolNumber == section.Index + 1
                && section.FindAtom(section.Address + (ulong)relocation.Address) is { } own)
            {
                // The subtrahend is the label, and the content contains minus its address. Such a label belongs to
                // the atom of the field itself (a table whose entries are relative to its start), so the atom can
                // stand in for it once the address of the atom is added back.
                return new MachRelocation(
                    (uint)relocation.Address, MachRelocKind.Pointer, relocation.Length, ExternSymbol(target, file, section, relocation),
                    value + (long)own.InputAddress, file.AnchorFor(own));
            }

            // With two labels the content mixes both addresses, which cannot be taken apart again. Compilers only
            // produce that form in debug information.
            throw Unsupported(target, section, sub, "difference of two labels without symbols");
        }

        if (relocation.IsExtern)
        {
            return new MachRelocation((uint)relocation.Address, MachRelocKind.Pointer, relocation.Length, ExternSymbol(target, file, section, relocation), value);
        }

        var (anchor, addend) = AnchorAt(target, file, section, relocation, relocation.Length == 3 ? (ulong)value : (uint)value);
        return new MachRelocation((uint)relocation.Address, MachRelocKind.Pointer, relocation.Length, anchor, addend);
    }

    private static void ScanPointer(MachLinkContext context, Atom atom, in MachRelocation relocation, MachSymbol symbol)
    {
        if (relocation.SubtrahendIndex >= 0)
        {
            var subtrahend = atom.File.Symbols[relocation.SubtrahendIndex];
            if (symbol.IsImported || subtrahend.IsImported)
            {
                throw new LinkerException(
                    $"{atom.DisplayName}: difference of '{symbol.Name}' and '{subtrahend.Name}' is not known before the image is loaded");
            }

            return;
        }

        if (relocation.Length != 3)
        {
            if (symbol.IsImageRelative || symbol.IsImported)
            {
                throw new LinkerException($"{atom.DisplayName}: 32-bit absolute address of '{symbol.Name}' in a position independent image");
            }

            return;
        }

        if (symbol.IsImported)
        {
            context.AddBind(atom, relocation.Offset, symbol, relocation.Addend);
        }
        else if (symbol.IsImageRelative && !IsThreadLocalOffset(atom, symbol))
        {
            context.AddRebase(atom, relocation.Offset, symbol);
        }
    }
}
