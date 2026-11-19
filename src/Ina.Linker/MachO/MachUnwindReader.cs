using Ina.Linker.Elf;

namespace Ina.Linker.MachO;

/// <summary>
/// How to unwind out of one range of code: an entry of <c>__LD,__compact_unwind</c>, or an FDE of
/// <c>__eh_frame</c> that has no such entry.
/// </summary>
internal sealed class UnwindRecord
{
    /// <summary>The atom that contains the code.</summary>
    public required Atom Function { get; init; }

    /// <summary>Start of the range inside <see cref="Function"/>.</summary>
    public uint Offset { get; init; }

    public uint Length { get; init; }

    /// <summary>The compact unwind encoding as the compiler wrote it, without personality index and FDE offset.</summary>
    public uint Encoding { get; init; }

    /// <summary>Index of the personality function in the symbols of the object file, or -1.</summary>
    public int PersonalityIndex { get; init; } = -1;

    /// <summary>The atom with the language specific data (exception tables, GC information) of the range.</summary>
    public Atom? Lsda { get; init; }

    public uint LsdaOffset { get; init; }

    /// <summary>The DWARF description of the range, when the encoding defers to it.</summary>
    public EhFrameFde? Fde { get; init; }

    public ulong Address => Function.Address + Offset;
}

/// <summary>A common information entry of <c>__eh_frame</c>.</summary>
internal sealed class EhFrameCie
{
    public required MachInputSection Section { get; init; }

    public int Offset { get; init; }

    public int Length { get; init; }

    /// <summary>How FDEs of this CIE encode the address of their code (DW_EH_PE_*).</summary>
    public byte PointerEncoding { get; init; }

    /// <summary>How FDEs encode the address of their language specific data; 0xff when they have none.</summary>
    public byte LsdaEncoding { get; init; } = EhFrameEncoding.Omit;

    public bool HasAugmentationData { get; init; }

    /// <summary>Offset of the personality pointer inside the section, or -1.</summary>
    public int PersonalityFieldOffset { get; init; } = -1;

    public byte PersonalityEncoding { get; init; }

    /// <summary>Index of the personality function in the symbols of the object file, or -1.</summary>
    public int PersonalityIndex { get; init; } = -1;

    /// <summary>Offset in the output <c>__eh_frame</c>, or -1 while the CIE is not part of the output.</summary>
    public long OutputOffset { get; set; } = -1;
}

/// <summary>A frame description entry of <c>__eh_frame</c>.</summary>
internal sealed class EhFrameFde
{
    public required MachInputSection Section { get; init; }

    public required EhFrameCie Cie { get; init; }

    public int Offset { get; init; }

    public int Length { get; init; }

    /// <summary>Offset of the field with the code address inside the section.</summary>
    public int PcBeginFieldOffset { get; init; }

    public required Atom Function { get; init; }

    public uint FunctionOffset { get; init; }

    public uint PcRange { get; init; }

    /// <summary>Offset of the field with the address of the language specific data inside the section, or -1.</summary>
    public int LsdaFieldOffset { get; init; } = -1;

    public Atom? Lsda { get; init; }

    public uint LsdaOffset { get; init; }

    public long OutputOffset { get; set; } = -1;
}

/// <summary>Pointer encodings of <c>__eh_frame</c> (DW_EH_PE_*).</summary>
internal static class EhFrameEncoding
{
    public const byte Omit = 0xff;
    public const byte FormatMask = 0x0f;
    public const byte ApplicationMask = 0x70;
    public const byte PcRel = 0x10;
    public const byte Indirect = 0x80;

    /// <summary>The size of a field with the given encoding, or 0 when the linker cannot rewrite it.</summary>
    public static int Size(byte encoding) => (encoding & FormatMask) switch
    {
        0x00 or 0x04 or 0x0c => 8, // absptr, udata8, sdata8
        0x03 or 0x0b => 4,         // udata4, sdata4
        _ => 0,
    };
}

/// <summary>
/// Reads the unwind tables of an object file and attaches a <see cref="UnwindRecord"/> to the atom of every
/// described range. The records follow their code through dead stripping and layout; the output tables
/// (<c>__unwind_info</c> and <c>__eh_frame</c>) are generated from those that remain.
/// </summary>
internal static class MachUnwindReader
{
    public static void Read(MachObjectFile file)
    {
        MachInputSection? ehFrame = null;
        MachInputSection? compactUnwind = null;
        foreach (var section in file.Sections)
        {
            if (section.Kind == MachSectionKind.EhFrame)
            {
                ehFrame = section;
            }
            else if (section.Kind == MachSectionKind.CompactUnwind)
            {
                compactUnwind = section;
            }
        }

        var fdes = ehFrame is null ? [] : ReadEhFrame(file, ehFrame);
        var fdeByFunction = new Dictionary<(Atom, uint), EhFrameFde>();
        foreach (var fde in fdes)
        {
            fdeByFunction.TryAdd((fde.Function, fde.FunctionOffset), fde);
        }

        var described = new HashSet<(Atom, uint)>();
        if (compactUnwind is not null)
        {
            ReadCompactUnwind(file, compactUnwind, fdeByFunction, described);
        }

        // Code that only has an FDE is described through it. An FDE of code with a self-contained compact encoding
        // is redundant, since the unwinder never looks at it.
        var dwarfMode = MachTarget.ForCpu(file.CpuType, file.Name).DwarfUnwindMode;
        foreach (var fde in fdes)
        {
            if (described.Add((fde.Function, fde.FunctionOffset)))
            {
                Attach(new UnwindRecord { Function = fde.Function, Offset = fde.FunctionOffset, Length = fde.PcRange, Encoding = dwarfMode, Fde = fde });
            }
        }
    }

    private static void Attach(UnwindRecord record) => (record.Function.UnwindRecords ??= []).Add(record);

    private static void ReadCompactUnwind(
        MachObjectFile file, MachInputSection section, Dictionary<(Atom, uint), EhFrameFde> fdeByFunction, HashSet<(Atom, uint)> described)
    {
        var content = section.Content.Span;
        var dwarfMode = MachTarget.ForCpu(file.CpuType, file.Name).DwarfUnwindMode;
        for (var offset = 0; offset + CompactUnwind.EntrySize <= content.Length; offset += CompactUnwind.EntrySize)
        {
            var function = file.EvaluateField(section, offset, 8);
            if (function.ExternalSymbol >= 0 || function.IsGotReference)
            {
                throw new LinkerException($"{section.DisplayName}+0x{offset:x}: unwind entry for code outside of the object file");
            }

            // Code in a section that does not take part in the link needs no unwind information.
            if (file.FindAtom((ulong)function.Value) is not { } atom)
            {
                continue;
            }

            var functionOffset = (uint)((ulong)function.Value - atom.InputAddress);
            var encoding = LittleEndian.U32(content, offset + 12);

            var personality = file.EvaluateField(section, offset + 16, 8);
            var personalityIndex = personality.ExternalSymbol;
            if (personalityIndex < 0 && (personality.HasRelocation || personality.Value != 0))
            {
                var personalityAtom = file.FindAtom((ulong)personality.Value)
                    ?? throw new LinkerException($"{section.DisplayName}+0x{offset:x}: personality function is not inside a section of the object file");
                personalityIndex = file.AnchorFor(personalityAtom);
            }

            Atom? lsda = null;
            var lsdaOffset = 0u;
            var lsdaField = file.EvaluateField(section, offset + 24, 8);
            if (lsdaField.ExternalSymbol >= 0)
            {
                throw new LinkerException($"{section.DisplayName}+0x{offset:x}: language specific data outside of the object file");
            }

            if (lsdaField.HasRelocation || lsdaField.Value != 0)
            {
                lsda = file.FindAtom((ulong)lsdaField.Value)
                    ?? throw new LinkerException($"{section.DisplayName}+0x{offset:x}: language specific data is not inside a section of the object file");
                lsdaOffset = (uint)((ulong)lsdaField.Value - lsda.InputAddress);
            }

            EhFrameFde? fde = null;
            if ((encoding & CompactUnwind.ModeMask) == dwarfMode && !fdeByFunction.TryGetValue((atom, functionOffset), out fde))
            {
                // Without the FDE that the entry defers to, nothing is known about the frame.
                encoding = 0;
            }

            described.Add((atom, functionOffset));
            Attach(new UnwindRecord
            {
                Function = atom,
                Offset = functionOffset,
                Length = LittleEndian.U32(content, offset + 8),
                Encoding = encoding,
                PersonalityIndex = personalityIndex,
                Lsda = lsda,
                LsdaOffset = lsdaOffset,
                Fde = fde,
            });
        }
    }

    /// <summary>Splits <c>__eh_frame</c> into its records and finds out which code and data each FDE refers to.</summary>
    private static List<EhFrameFde> ReadEhFrame(MachObjectFile file, MachInputSection section)
    {
        var content = section.Content.Span;
        var cies = new Dictionary<int, EhFrameCie>();
        var fdes = new List<EhFrameFde>();

        for (var offset = 0; offset + 4 <= content.Length;)
        {
            var length = LittleEndian.U32(content, offset);
            if (length == 0)
            {
                break;
            }

            if (length == 0xffffffff || offset + 4 + (long)length > content.Length || length < 4)
            {
                throw new LinkerException($"{section.DisplayName}+0x{offset:x}: malformed unwind record");
            }

            var size = 4 + (int)length;
            var id = LittleEndian.U32(content, offset + 4);
            if (id == 0)
            {
                cies[offset] = ReadCie(file, section, offset, size);
            }
            else
            {
                // The CIE is identified by its distance from the field that names it.
                var cieOffset = offset + 4 - (int)id;
                if (!cies.TryGetValue(cieOffset, out var cie))
                {
                    throw new LinkerException($"{section.DisplayName}+0x{offset:x}: unwind record refers to a missing CIE");
                }

                if (ReadFde(file, section, cie, offset, size) is { } fde)
                {
                    fdes.Add(fde);
                }
            }

            offset += size;
        }

        return fdes;
    }

    private static EhFrameCie ReadCie(MachObjectFile file, MachInputSection section, int offset, int size)
    {
        var content = section.Content.Span;
        var end = offset + size;
        var position = offset + 8;
        var version = content[position++];
        var augmentationStart = position;
        while (position < end && content[position] != 0)
        {
            position++;
        }

        var augmentation = content[augmentationStart..position];
        position++;
        ReadUleb(content, ref position);       // code alignment factor
        ReadUleb(content, ref position);       // data alignment factor (signed, but only skipped here)
        if (version == 1)
        {
            position++;                        // return address register
        }
        else
        {
            ReadUleb(content, ref position);
        }

        var pointerEncoding = (byte)0;
        var lsdaEncoding = EhFrameEncoding.Omit;
        var personalityEncoding = (byte)0;
        var personalityField = -1;
        var personalityIndex = -1;
        var hasAugmentationData = augmentation.Length > 0 && augmentation[0] == (byte)'z';
        if (hasAugmentationData)
        {
            ReadUleb(content, ref position);   // length of the augmentation data
            foreach (var letter in augmentation[1..])
            {
                switch ((char)letter)
                {
                    case 'R':
                        pointerEncoding = content[position++];
                        break;
                    case 'L':
                        lsdaEncoding = content[position++];
                        break;
                    case 'P':
                        personalityEncoding = content[position++];
                        personalityField = position;
                        position += EhFrameEncoding.Size(personalityEncoding);
                        break;
                    case 'S' or 'B' or 'G':
                        break;
                    default:
                        throw new LinkerException($"{section.DisplayName}+0x{offset:x}: unsupported CIE augmentation '{(char)letter}'");
                }
            }
        }
        else if (augmentation.Length > 0)
        {
            throw new LinkerException($"{section.DisplayName}+0x{offset:x}: unsupported CIE augmentation");
        }

        if (EhFrameEncoding.Size(pointerEncoding) == 0 || (pointerEncoding & EhFrameEncoding.ApplicationMask) != EhFrameEncoding.PcRel)
        {
            // An absolute address would have to be adjusted at load time, which the read-only text segment forbids.
            throw new LinkerException($"{section.DisplayName}+0x{offset:x}: unsupported pointer encoding 0x{pointerEncoding:x2} in unwind information");
        }

        if (personalityField >= 0)
        {
            var field = file.EvaluateField(section, personalityField, EhFrameEncoding.Size(personalityEncoding));
            const byte expected = EhFrameEncoding.Indirect | EhFrameEncoding.PcRel;
            if (!field.IsGotReference || (personalityEncoding & (EhFrameEncoding.Indirect | EhFrameEncoding.ApplicationMask)) != expected)
            {
                throw new LinkerException($"{section.DisplayName}+0x{offset:x}: the personality function must be referenced through the GOT");
            }

            personalityIndex = field.ExternalSymbol;
        }

        return new EhFrameCie
        {
            Section = section,
            Offset = offset,
            Length = size,
            PointerEncoding = pointerEncoding,
            LsdaEncoding = lsdaEncoding,
            HasAugmentationData = hasAugmentationData,
            PersonalityFieldOffset = personalityField,
            PersonalityEncoding = personalityEncoding,
            PersonalityIndex = personalityIndex,
        };
    }

    private static EhFrameFde? ReadFde(MachObjectFile file, MachInputSection section, EhFrameCie cie, int offset, int size)
    {
        var content = section.Content.Span;
        var position = offset + 8;
        var pointerSize = EhFrameEncoding.Size(cie.PointerEncoding);

        var pcBeginField = position;
        var pcBegin = ResolveAddress(file, section, pcBeginField, pointerSize, offset);
        position += pointerSize;
        var pcRange = pointerSize == 8 ? LittleEndian.U64(content, position) : LittleEndian.U32(content, position);
        position += pointerSize;

        // An FDE for code in a section that does not take part in the link is dropped with that code.
        if (pcBegin is not { } codeAddress || file.FindAtom(codeAddress) is not { } function)
        {
            return null;
        }

        var lsdaField = -1;
        Atom? lsda = null;
        var lsdaOffset = 0u;
        if (cie.HasAugmentationData)
        {
            var augmentationLength = ReadUleb(content, ref position);
            if (cie.LsdaEncoding != EhFrameEncoding.Omit && augmentationLength > 0)
            {
                var lsdaSize = EhFrameEncoding.Size(cie.LsdaEncoding);
                if (lsdaSize == 0 || (cie.LsdaEncoding & EhFrameEncoding.ApplicationMask) != EhFrameEncoding.PcRel)
                {
                    throw new LinkerException($"{section.DisplayName}+0x{offset:x}: unsupported pointer encoding 0x{cie.LsdaEncoding:x2} in unwind information");
                }

                lsdaField = position;
                if (ResolveAddress(file, section, lsdaField, lsdaSize, offset) is { } lsdaAddress)
                {
                    lsda = file.FindAtom(lsdaAddress)
                        ?? throw new LinkerException($"{section.DisplayName}+0x{offset:x}: language specific data is not inside a section of the object file");
                    lsdaOffset = (uint)(lsdaAddress - lsda.InputAddress);
                }
            }
        }

        return new EhFrameFde
        {
            Section = section,
            Cie = cie,
            Offset = offset,
            Length = size,
            PcBeginFieldOffset = pcBeginField,
            Function = function,
            FunctionOffset = (uint)(codeAddress - function.InputAddress),
            PcRange = (uint)pcRange,
            LsdaFieldOffset = lsdaField,
            Lsda = lsda,
            LsdaOffset = lsdaOffset,
        };
    }

    /// <summary>
    /// Returns the address (inside the object file) that a PC-relative field refers to, or null for an empty field.
    /// </summary>
    private static ulong? ResolveAddress(MachObjectFile file, MachInputSection section, int fieldOffset, int size, int recordOffset)
    {
        var field = file.EvaluateField(section, fieldOffset, size);
        if (field.ExternalSymbol >= 0)
        {
            throw new LinkerException($"{section.DisplayName}+0x{recordOffset:x}: unwind record refers to a symbol outside of the object file");
        }

        if (!field.HasRelocation && field.Value == 0)
        {
            return null;
        }

        return section.Address + (ulong)fieldOffset + (ulong)field.Value;
    }

    private static ulong ReadUleb(ReadOnlySpan<byte> data, ref int position)
    {
        var result = 0UL;
        for (var shift = 0; ; shift += 7)
        {
            var value = data[position++];
            result |= (ulong)(value & 0x7f) << shift;
            if ((value & 0x80) == 0)
            {
                return result;
            }
        }
    }
}
