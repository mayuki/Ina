using Ina.Linker.Elf;
using Ina.Linker.Input;

namespace Ina.Linker.Output;

/// <summary>The output <c>.eh_frame</c>: the surviving CIE and FDE records of all inputs, followed by a terminator.</summary>
internal sealed class EhFrameChunk() : OutputChunk(".eh_frame", SectionType.ProgBits, SectionFlags.Alloc, 8)
{
    private const int TerminatorSize = 4;
    private readonly List<EhFrameRecord> _records = [];

    public IReadOnlyList<EhFrameRecord> Records => _records;

    /// <summary>Appends a live record. A CIE must be added before the FDEs that refer to it.</summary>
    public void Add(EhFrameRecord record)
    {
        record.Parent = this;
        record.Offset = Size == 0 ? 0 : Size - TerminatorSize;
        _records.Add(record);
        Size = record.Offset + (ulong)record.Size + TerminatorSize;
    }

    public override void Write(Span<byte> destination, LinkContext context)
    {
        foreach (var record in _records)
        {
            var target = destination.Slice((int)record.Offset, record.Size);
            record.Content.CopyTo(target);
            if (record.Cie is { } cie)
            {
                // The CIE pointer is relative to its own position, which changed when dead records were dropped.
                LittleEndian.U32(target, 4, (uint)(record.Offset + 4 - cie.Offset));
            }

            context.ApplyRelocations(record, target);
        }

        // The trailing zero length word that ends the section is already zero in the output buffer.
    }
}

/// <summary>
/// The binary search table over all FDEs (<c>.eh_frame_hdr</c>). Unwinders, including the one built into the
/// .NET runtime, find it through the PT_GNU_EH_FRAME program header and use it to locate the unwind info of a frame.
/// </summary>
internal sealed class EhFrameHeaderChunk(EhFrameChunk frames) : OutputChunk(".eh_frame_hdr", SectionType.ProgBits, SectionFlags.Alloc, 4)
{
    private const byte EncodingPcRelSData4 = 0x1b;
    private const byte EncodingUData4 = 0x03;
    private const byte EncodingDataRelSData4 = 0x3b;

    private List<EhFrameRecord> _fdes = [];

    public void Finish()
    {
        _fdes = frames.Records.Where(static r => r is { IsCie: false, PcBeginRelocation: not null }).ToList();
        Size = (ulong)(12 + _fdes.Count * 8);
    }

    public override void Write(Span<byte> destination, LinkContext context)
    {
        destination[0] = 1; // version
        destination[1] = EncodingPcRelSData4; // encoding of the .eh_frame pointer
        destination[2] = EncodingUData4; // encoding of the FDE count
        destination[3] = EncodingDataRelSData4; // encoding of the table entries
        LittleEndian.I32(destination, 4, checked((int)((long)frames.Address - (long)(Address + 4))));
        LittleEndian.U32(destination, 8, (uint)_fdes.Count);

        var entries = new (long InitialLocation, long Fde)[_fdes.Count];
        for (var i = 0; i < _fdes.Count; i++)
        {
            var fde = _fdes[i];
            var relocation = fde.PcBeginRelocation!.Value;
            var start = fde.File.Symbols[relocation.SymbolIndex].Address + (ulong)relocation.Addend;
            entries[i] = ((long)start - (long)Address, (long)fde.Address - (long)Address);
        }

        Array.Sort(entries, static (a, b) => a.InitialLocation.CompareTo(b.InitialLocation));
        for (var i = 0; i < entries.Length; i++)
        {
            LittleEndian.I32(destination, 12 + i * 8, checked((int)entries[i].InitialLocation));
            LittleEndian.I32(destination, 16 + i * 8, checked((int)entries[i].Fde));
        }
    }
}
