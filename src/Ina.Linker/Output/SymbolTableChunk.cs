using Ina.Linker.Elf;
using Ina.Linker.Symbols;

namespace Ina.Linker.Output;

/// <summary>The optional static symbol table (<c>.symtab</c>) used by debuggers and profilers.</summary>
internal sealed class SymbolTableChunk : OutputChunk
{
    private const int EntryLength = 24;
    private readonly List<(Symbol Symbol, uint NameOffset)> _symbols = [];
    private readonly StringTableChunk _strings;

    public SymbolTableChunk(StringTableChunk strings)
        : base(".symtab", SectionType.SymTab, 0, 8)
    {
        _strings = strings;
        LinkedChunk = strings;
        EntrySize = EntryLength;
        Size = EntryLength;
    }

    public void Add(Symbol symbol)
    {
        _symbols.Add((symbol, _strings.Add(symbol.Name)));
        Size = (ulong)((_symbols.Count + 1) * EntryLength);
    }

    /// <summary>Orders local symbols first, as the ELF specification requires.</summary>
    public void Finish()
    {
        _symbols.Sort(static (a, b) => b.Symbol.IsLocal.CompareTo(a.Symbol.IsLocal));
        Info = (uint)(_symbols.Count(static s => s.Symbol.IsLocal) + 1);
    }

    public override void Write(Span<byte> destination, LinkContext context)
    {
        for (var i = 0; i < _symbols.Count; i++)
        {
            var (symbol, nameOffset) = _symbols[i];
            var entry = destination.Slice((i + 1) * EntryLength, EntryLength);
            var binding = symbol.IsLocal ? SymbolBinding.Local : symbol.IsWeak ? SymbolBinding.Weak : SymbolBinding.Global;
            LittleEndian.U32(entry, 0, nameOffset);
            entry[4] = (byte)((binding << 4) | symbol.Type);
            entry[5] = symbol.Visibility;
            LittleEndian.U16(entry, 6, (ushort)(symbol.Section?.Parent?.SectionIndex ?? Elf.SectionIndex.Abs));
            LittleEndian.U64(entry, 8, symbol.Address);
            LittleEndian.U64(entry, 16, symbol.Size);
        }
    }
}
