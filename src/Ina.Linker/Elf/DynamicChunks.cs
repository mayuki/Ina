
namespace Ina.Linker.Elf;

/// <summary>The dynamic symbol table: imported symbols plus the few symbols the executable exports.</summary>
internal sealed class DynamicSymbolTable : OutputChunk
{
    private const int EntryLength = 24;
    private readonly List<Symbol> _symbols = [];
    private readonly StringTableChunk _strings;
    private readonly List<uint> _nameOffsets = [];

    public DynamicSymbolTable(StringTableChunk strings)
        : base(".dynsym", SectionType.DynSym, SectionFlags.Alloc, 8)
    {
        _strings = strings;
        LinkedChunk = strings;
        EntrySize = EntryLength;
        Info = 1; // one local symbol: the mandatory null entry
        Size = EntryLength;
    }

    /// <summary>The symbols in table order; index 0 (the null symbol) is not included.</summary>
    public IReadOnlyList<Symbol> Symbols => _symbols;

    public int Count => _symbols.Count + 1;

    public void Add(Symbol symbol)
    {
        if (symbol.DynamicIndex >= 0)
        {
            return;
        }

        symbol.DynamicIndex = Count;
        _symbols.Add(symbol);
        _nameOffsets.Add(_strings.Add(symbol.Name));
        Size = (ulong)(Count * EntryLength);
    }

    public override void Write(Span<byte> destination, ElfLinkContext context)
    {
        for (var i = 0; i < _symbols.Count; i++)
        {
            var symbol = _symbols[i];
            var entry = destination.Slice((i + 1) * EntryLength, EntryLength);
            LittleEndian.U32(entry, 0, _nameOffsets[i]);
            if (symbol.IsImported)
            {
                var type = symbol.Import!.Kind switch
                {
                    Abi.ImportedSymbolKind.Function => SymbolType.Func,
                    Abi.ImportedSymbolKind.Object => SymbolType.Object,
                    _ => SymbolType.Tls,
                };

                // A symbol that is only referenced weakly may be missing at run time without failing the load.
                var binding = symbol.HasStrongReference ? SymbolBinding.Global : SymbolBinding.Weak;
                entry[4] = (byte)((binding << 4) | type);
            }
            else
            {
                entry[4] = (byte)((SymbolBinding.Global << 4) | symbol.Type);
                LittleEndian.U16(entry, 6, (ushort)(symbol.Section?.Parent?.SectionIndex ?? Elf.SectionIndex.Abs));
                LittleEndian.U64(entry, 8, symbol.Address);
                LittleEndian.U64(entry, 16, symbol.Size);
            }
        }
    }
}

/// <summary>The classic System V hash table. Every dynamic loader understands it and it needs no symbol ordering.</summary>
internal sealed class HashChunk : OutputChunk
{
    private readonly DynamicSymbolTable _symbols;

    public HashChunk(DynamicSymbolTable symbols)
        : base(".hash", SectionType.Hash, SectionFlags.Alloc, 8)
    {
        _symbols = symbols;
        LinkedChunk = symbols;
        EntrySize = 4;
    }

    public void Finish() => Size = (ulong)(2 + BucketCount + _symbols.Count) * 4;

    private int BucketCount => Math.Max(1, _symbols.Count / 2);

    public override void Write(Span<byte> destination, ElfLinkContext context)
    {
        var bucketCount = BucketCount;
        LittleEndian.U32(destination, 0, (uint)bucketCount);
        LittleEndian.U32(destination, 4, (uint)_symbols.Count);
        var buckets = destination[8..];
        var chains = destination[(8 + bucketCount * 4)..];
        for (var i = 0; i < _symbols.Symbols.Count; i++)
        {
            var index = i + 1;
            var bucket = (int)(Hash(_symbols.Symbols[i].Name) % (uint)bucketCount);

            // Prepend to the chain of the bucket.
            LittleEndian.U32(chains, index * 4, LittleEndian.U32(buckets, bucket * 4));
            LittleEndian.U32(buckets, bucket * 4, (uint)index);
        }
    }

    /// <summary>The ELF hash function, also used for symbol version names.</summary>
    public static uint Hash(string name)
    {
        uint hash = 0;
        foreach (var c in System.Text.Encoding.UTF8.GetBytes(name))
        {
            hash = (hash << 4) + c;
            var high = hash & 0xf0000000;
            if (high != 0)
            {
                hash ^= high >> 24;
            }

            hash &= ~high;
        }

        return hash;
    }
}

/// <summary>
/// Symbol versioning: <c>.gnu.version</c> assigns a version index to every dynamic symbol and
/// <c>.gnu.version_r</c> lists the versions required from each shared library.
/// </summary>
internal sealed class VersionTables
{
    private const ushort VersionIndexLocal = 0;
    private const ushort VersionIndexGlobal = 1;

    private readonly DynamicSymbolTable _symbols;
    private readonly StringTableChunk _strings;
    private readonly List<(string Library, List<(string Version, ushort Index)> Versions)> _needs = [];
    private ushort[] _symbolVersions = [];

    public VersionTables(DynamicSymbolTable symbols, StringTableChunk strings)
    {
        _symbols = symbols;
        _strings = strings;
        VersionSymbols = new Chunk(".gnu.version", SectionType.GnuVerSym, 2, 2, WriteVersionSymbols) { LinkedChunk = symbols };
        VersionNeeds = new Chunk(".gnu.version_r", SectionType.GnuVerNeed, 8, 0, WriteVersionNeeds) { LinkedChunk = strings };
    }

    public Chunk VersionSymbols { get; }

    public Chunk VersionNeeds { get; }

    public int NeedCount => _needs.Count;

    /// <summary>All "library:version" pairs the image requires.</summary>
    public IEnumerable<string> RequiredVersions => _needs.SelectMany(static n => n.Versions.Select(v => $"{n.Library}:{v.Version}"));

    public void Finish()
    {
        _symbolVersions = new ushort[_symbols.Count];
        _symbolVersions[0] = VersionIndexLocal;
        ushort nextIndex = 2;
        for (var i = 0; i < _symbols.Symbols.Count; i++)
        {
            var symbol = _symbols.Symbols[i];
            if (symbol.Import is not { Version: { } version } import)
            {
                _symbolVersions[i + 1] = VersionIndexGlobal;
                continue;
            }

            var need = _needs.Find(n => n.Library == import.Library);
            if (need.Library is null)
            {
                need = (import.Library, []);
                _needs.Add(need);
                _strings.Add(import.Library);
            }

            var entry = need.Versions.Find(v => v.Version == version);
            if (entry.Version is null)
            {
                entry = (version, nextIndex++);
                need.Versions.Add(entry);
                _strings.Add(version);
            }

            _symbolVersions[i + 1] = entry.Index;
        }

        VersionSymbols.SetSize((ulong)_symbolVersions.Length * 2);
        VersionNeeds.SetSize((ulong)(_needs.Count * 16 + _needs.Sum(static n => n.Versions.Count) * 16));
        VersionNeeds.Info = (uint)_needs.Count;
    }

    private void WriteVersionSymbols(Span<byte> destination)
    {
        for (var i = 0; i < _symbolVersions.Length; i++)
        {
            LittleEndian.U16(destination, i * 2, _symbolVersions[i]);
        }
    }

    private void WriteVersionNeeds(Span<byte> destination)
    {
        var offset = 0;
        for (var i = 0; i < _needs.Count; i++)
        {
            var (library, versions) = _needs[i];
            var size = 16 + versions.Count * 16;

            // Elf64_Verneed: vn_version, vn_cnt, vn_file, vn_aux, vn_next
            LittleEndian.U16(destination, offset, 1);
            LittleEndian.U16(destination, offset + 2, (ushort)versions.Count);
            LittleEndian.U32(destination, offset + 4, _strings.Add(library));
            LittleEndian.U32(destination, offset + 8, 16);
            LittleEndian.U32(destination, offset + 12, i == _needs.Count - 1 ? 0 : (uint)size);

            for (var j = 0; j < versions.Count; j++)
            {
                // Elf64_Vernaux: vna_hash, vna_flags, vna_other, vna_name, vna_next
                var aux = offset + 16 + j * 16;
                LittleEndian.U32(destination, aux, HashChunk.Hash(versions[j].Version));
                LittleEndian.U16(destination, aux + 4, 0);
                LittleEndian.U16(destination, aux + 6, versions[j].Index);
                LittleEndian.U32(destination, aux + 8, _strings.Add(versions[j].Version));
                LittleEndian.U32(destination, aux + 12, j == versions.Count - 1 ? 0 : 16u);
            }

            offset += size;
        }
    }

    internal sealed class Chunk(string name, uint type, ulong alignment, ulong entrySize, Chunk.Writer writer)
        : OutputChunk(name, type, SectionFlags.Alloc, alignment)
    {
        public delegate void Writer(Span<byte> destination);

        public void SetSize(ulong size)
        {
            Size = size;
            EntrySize = entrySize;
        }

        public override void Write(Span<byte> destination, ElfLinkContext context) => writer(destination);
    }
}

/// <summary>The table of relocations applied by the dynamic loader (<c>.rela.dyn</c>).</summary>
internal sealed class RelaDynChunk : OutputChunk
{
    private const int EntryLength = 24;
    private List<DynamicRelocation> _relocations = [];

    public RelaDynChunk(DynamicSymbolTable symbols)
        : base(".rela.dyn", SectionType.Rela, SectionFlags.Alloc, 8)
    {
        LinkedChunk = symbols;
        EntrySize = EntryLength;
    }

    /// <summary>Number of relative relocations; they are sorted to the front so the loader can batch them.</summary>
    public int RelativeCount { get; private set; }

    public void Finish(List<DynamicRelocation> relocations, uint relativeType)
    {
        _relocations = relocations;
        RelativeCount = relocations.Count(r => r.Type == relativeType);
        Size = (ulong)(relocations.Count * EntryLength);
    }

    public override void Write(Span<byte> destination, ElfLinkContext context)
    {
        var relativeType = context.Target.RelativeRelocation;
        var ordered = _relocations
            .OrderBy(r => r.Type == relativeType ? 0 : 1)
            .ThenBy(static r => r.DynamicSymbol?.DynamicIndex ?? 0)
            .ThenBy(static r => r.Address);

        var offset = 0;
        foreach (var relocation in ordered)
        {
            var symbolIndex = (ulong)(relocation.DynamicSymbol?.DynamicIndex ?? 0);
            var addend = relocation.Target is { } target ? (long)target.Address + relocation.Addend : relocation.Addend;
            LittleEndian.U64(destination, offset, relocation.Address);
            LittleEndian.U64(destination, offset + 8, (symbolIndex << 32) | relocation.Type);
            LittleEndian.I64(destination, offset + 16, addend);
            offset += EntryLength;
        }
    }
}

/// <summary>The <c>.dynamic</c> section that tells the loader where everything is.</summary>
internal sealed class DynamicChunk : OutputChunk
{
    private readonly List<(long Tag, Func<ulong> Value)> _entries = [];

    public DynamicChunk(StringTableChunk strings)
        : base(".dynamic", SectionType.Dynamic, SectionFlags.Alloc | SectionFlags.Write, 8)
    {
        LinkedChunk = strings;
        EntrySize = 16;
        Size = 16;
    }

    public void Add(long tag, Func<ulong> value)
    {
        _entries.Add((tag, value));
        Size = (ulong)(_entries.Count + 1) * 16;
    }

    public void Add(long tag, ulong value) => Add(tag, () => value);

    public override void Write(Span<byte> destination, ElfLinkContext context)
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            LittleEndian.I64(destination, i * 16, _entries[i].Tag);
            LittleEndian.U64(destination, i * 16 + 8, _entries[i].Value());
        }
    }
}
