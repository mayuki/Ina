using Ina.Linker.Elf;
using Ina.Linker.Symbols;

namespace Ina.Linker.Output;

/// <summary>
/// Stubs for calls to imported functions. Every stub jumps through a GOT entry that the loader fills in at startup,
/// so there is no lazy binding and no PLT header.
/// </summary>
internal sealed class PltSection() : OutputChunk(".plt", SectionType.ProgBits, SectionFlags.Alloc | SectionFlags.ExecInstr, 16)
{
    private readonly List<Symbol> _symbols = [];
    private int _entrySize;

    public void Add(Symbol symbol, LinkContext context)
    {
        if (symbol.PltIndex >= 0)
        {
            return;
        }

        context.Got.AddAddress(symbol, context);
        symbol.PltIndex = _symbols.Count;
        _symbols.Add(symbol);
        _entrySize = context.Target.PltEntrySize;
        Size = (ulong)(_symbols.Count * _entrySize);
        EntrySize = (ulong)_entrySize;
    }

    public ulong EntryAddress(Symbol symbol) => Address + (ulong)(symbol.PltIndex * _entrySize);

    public override void Write(Span<byte> destination, LinkContext context)
    {
        foreach (var symbol in _symbols)
        {
            var entry = destination.Slice(symbol.PltIndex * _entrySize, _entrySize);
            context.Target.WritePltEntry(entry, EntryAddress(symbol), context.Got.EntryAddress(symbol.GotIndex));
        }
    }
}
