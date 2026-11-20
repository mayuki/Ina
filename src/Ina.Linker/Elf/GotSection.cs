
namespace Ina.Linker.Elf;

/// <summary>
/// The global offset table. Besides addresses of symbols it holds the thread-local storage descriptors; those are
/// filled in statically because an executable always is TLS module 1 and its TLS block has a fixed offset.
/// </summary>
internal sealed class GotSection() : OutputChunk(".got", SectionType.ProgBits, SectionFlags.Alloc | SectionFlags.Write, 8)
{
    private readonly List<Entry> _entries = [];

    private enum EntryKind
    {
        Address,
        TlsModule,
        TlsOffset,
        ThreadPointerOffset,
    }

    /// <summary>Index of the module/offset pair shared by all local-dynamic TLS accesses, or -1.</summary>
    public int TlsModuleIndex { get; private set; } = -1;

    public int Count => _entries.Count;

    public ulong EntryAddress(int index) => Address + (ulong)index * 8;

    /// <summary>Reserves an entry holding the address of <paramref name="symbol"/>.</summary>
    public void AddAddress(Symbol symbol, ElfLinkContext context)
    {
        if (symbol.GotIndex >= 0)
        {
            return;
        }

        symbol.GotIndex = Append(EntryKind.Address, symbol);
        var offset = (ulong)symbol.GotIndex * 8;
        if (symbol.IsImported)
        {
            context.DynamicRelocations.Add(new DynamicRelocation
            {
                Chunk = this, Offset = offset, Type = context.Target.GlobDatRelocation, DynamicSymbol = symbol,
            });
        }
        else if (context.IsPositionIndependent && symbol.IsImageRelative)
        {
            context.DynamicRelocations.Add(new DynamicRelocation
            {
                Chunk = this, Offset = offset, Type = context.Target.RelativeRelocation, Target = symbol,
            });
        }
    }

    /// <summary>Reserves a (module, offset) pair for a general-dynamic access to <paramref name="symbol"/>.</summary>
    public void AddTlsDescriptor(Symbol symbol)
    {
        if (symbol.TlsGdIndex >= 0)
        {
            return;
        }

        symbol.TlsGdIndex = Append(EntryKind.TlsModule, null);
        Append(EntryKind.TlsOffset, symbol);
    }

    /// <summary>Reserves the (module, 0) pair used by local-dynamic accesses.</summary>
    public void AddTlsModule()
    {
        if (TlsModuleIndex >= 0)
        {
            return;
        }

        TlsModuleIndex = Append(EntryKind.TlsModule, null);
        Append(EntryKind.TlsOffset, null);
    }

    /// <summary>Reserves an entry holding the thread pointer offset of <paramref name="symbol"/> (initial-exec).</summary>
    public void AddThreadPointerOffset(Symbol symbol)
    {
        if (symbol.GotTpIndex < 0)
        {
            symbol.GotTpIndex = Append(EntryKind.ThreadPointerOffset, symbol);
        }
    }

    public override void Write(Span<byte> destination, ElfLinkContext context)
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            var (kind, symbol) = _entries[i];
            var value = kind switch
            {
                EntryKind.Address => symbol!.IsImported ? 0 : symbol.Address,
                EntryKind.TlsModule => 1UL,
                EntryKind.TlsOffset => symbol is null ? 0 : symbol.Address - context.TlsStart,
                _ => (ulong)context.Target.ThreadPointerOffset(context, symbol!.Address),
            };
            LittleEndian.U64(destination, i * 8, value);
        }
    }

    private int Append(EntryKind kind, Symbol? symbol)
    {
        _entries.Add(new Entry(kind, symbol));
        Size = (ulong)_entries.Count * 8;
        EntrySize = 8;
        return _entries.Count - 1;
    }

    private readonly record struct Entry(EntryKind Kind, Symbol? Symbol);
}
