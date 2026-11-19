using System.Text;

namespace Ina.Linker.MachO;

/// <summary>A pointer in the image that the loader has to adjust or fill in, given as a position in a segment.</summary>
internal readonly record struct MachFixupLocation(int SegmentIndex, ulong SegmentOffset);

/// <summary>A pointer that the loader sets to the address of a symbol of a dynamic library.</summary>
internal readonly record struct MachBindFixup(MachFixupLocation Location, int LibraryOrdinal, string SymbolName, bool IsWeakImport, long Addend);

/// <summary>
/// Encoders for the tables in <c>__LINKEDIT</c> that <c>LC_DYLD_INFO_ONLY</c> points to. The loader interprets
/// them as small programs for a state machine (segment, offset, symbol, ...), which keeps them compact.
/// </summary>
internal static class MachLinkEdit
{
    private const int PointerSize = 8;

    /// <summary>
    /// Encodes the locations of the pointers into the image itself. They have to be adjusted when the image is
    /// loaded at a different address than it was linked for, which is always the case with address space layout
    /// randomization.
    /// </summary>
    public static byte[] EncodeRebases(List<MachFixupLocation> locations)
    {
        if (locations.Count == 0)
        {
            return [];
        }

        locations.Sort(static (a, b) => a.SegmentIndex != b.SegmentIndex ? a.SegmentIndex.CompareTo(b.SegmentIndex) : a.SegmentOffset.CompareTo(b.SegmentOffset));
        var output = new MemoryStream();
        output.WriteByte(DyldInfo.RebaseSetTypeImm | DyldInfo.TypePointer);

        var segment = -1;
        var cursor = 0UL;
        for (var i = 0; i < locations.Count;)
        {
            var location = locations[i];
            if (location.SegmentIndex != segment)
            {
                segment = location.SegmentIndex;
                output.WriteByte((byte)(DyldInfo.RebaseSetSegmentAndOffsetUleb | segment));
                WriteUleb(output, location.SegmentOffset);
            }
            else if (location.SegmentOffset != cursor)
            {
                output.WriteByte(DyldInfo.RebaseAddAddrUleb);
                WriteUleb(output, location.SegmentOffset - cursor);
            }

            // Tables of pointers are common, so a run of adjacent pointers is a single instruction.
            var count = 1;
            while (i + count < locations.Count && locations[i + count].SegmentIndex == segment
                && locations[i + count].SegmentOffset == location.SegmentOffset + (ulong)(count * PointerSize))
            {
                count++;
            }

            if (count < 16)
            {
                output.WriteByte((byte)(DyldInfo.RebaseDoRebaseImmTimes | count));
            }
            else
            {
                output.WriteByte(DyldInfo.RebaseDoRebaseUlebTimes);
                WriteUleb(output, (ulong)count);
            }

            cursor = location.SegmentOffset + (ulong)(count * PointerSize);
            i += count;
        }

        output.WriteByte(DyldInfo.RebaseDone);
        return Padded(output);
    }

    /// <summary>Encodes the locations at which the loader stores addresses of imported symbols.</summary>
    public static byte[] EncodeBinds(List<MachBindFixup> binds)
    {
        if (binds.Count == 0)
        {
            return [];
        }

        // Grouping by symbol lets consecutive fixups share the instructions that select library and symbol.
        binds.Sort(static (a, b) =>
        {
            var result = a.LibraryOrdinal.CompareTo(b.LibraryOrdinal);
            result = result != 0 ? result : string.CompareOrdinal(a.SymbolName, b.SymbolName);
            result = result != 0 ? result : a.Addend.CompareTo(b.Addend);
            result = result != 0 ? result : a.Location.SegmentIndex.CompareTo(b.Location.SegmentIndex);
            return result != 0 ? result : a.Location.SegmentOffset.CompareTo(b.Location.SegmentOffset);
        });

        var output = new MemoryStream();
        output.WriteByte(DyldInfo.BindSetTypeImm | DyldInfo.TypePointer);

        var ordinal = 0;
        string? name = null;
        var addend = 0L;
        var segment = -1;
        var cursor = 0UL;
        foreach (var bind in binds)
        {
            if (bind.LibraryOrdinal != ordinal)
            {
                ordinal = bind.LibraryOrdinal;
                if (ordinal < 16)
                {
                    output.WriteByte((byte)(DyldInfo.BindSetDylibOrdinalImm | ordinal));
                }
                else
                {
                    output.WriteByte(DyldInfo.BindSetDylibOrdinalUleb);
                    WriteUleb(output, (ulong)ordinal);
                }
            }

            if (bind.SymbolName != name)
            {
                name = bind.SymbolName;
                output.WriteByte((byte)(DyldInfo.BindSetSymbolTrailingFlagsImm | (bind.IsWeakImport ? DyldInfo.BindSymbolFlagsWeakImport : 0)));
                output.Write(Encoding.UTF8.GetBytes(name));
                output.WriteByte(0);
            }

            if (bind.Addend != addend)
            {
                addend = bind.Addend;
                output.WriteByte(DyldInfo.BindSetAddendSleb);
                WriteSleb(output, addend);
            }

            if (bind.Location.SegmentIndex != segment || bind.Location.SegmentOffset != cursor)
            {
                segment = bind.Location.SegmentIndex;
                output.WriteByte((byte)(DyldInfo.BindSetSegmentAndOffsetUleb | segment));
                WriteUleb(output, bind.Location.SegmentOffset);
            }

            output.WriteByte(DyldInfo.BindDoBind);
            cursor = bind.Location.SegmentOffset + PointerSize;
        }

        output.WriteByte(DyldInfo.BindDone);
        return Padded(output);
    }

    /// <summary>
    /// Builds the export trie: a prefix tree over the names of the exported symbols, which is what <c>dlsym</c>
    /// and other images search. Every terminal node holds the offset of the symbol from the start of the image.
    /// </summary>
    public static byte[] BuildExportTrie(IEnumerable<(string Name, ulong ImageOffset)> exports)
    {
        var root = new TrieNode();
        var any = false;
        foreach (var (name, offset) in exports.OrderBy(static e => e.Name, StringComparer.Ordinal))
        {
            Insert(root, name, offset);
            any = true;
        }

        if (!any)
        {
            return [];
        }

        var nodes = new List<TrieNode>();
        Flatten(root, nodes);

        // A node refers to its children by offset, and the size of an offset depends on its value. Repeat the
        // layout until nothing moves any more.
        for (var changed = true; changed;)
        {
            changed = false;
            var position = 0;
            foreach (var node in nodes)
            {
                if (node.Offset != position)
                {
                    node.Offset = position;
                    changed = true;
                }

                position += node.Size();
            }
        }

        var output = new MemoryStream();
        foreach (var node in nodes)
        {
            node.Write(output);
        }

        return Padded(output);
    }

    /// <summary>
    /// Encodes the start addresses of the functions as offsets from the start of the text segment. Debuggers and
    /// profilers use the table to find function boundaries when the symbol table is stripped.
    /// </summary>
    public static byte[] EncodeFunctionStarts(List<ulong> addresses, ulong textAddress)
    {
        if (addresses.Count == 0)
        {
            return [];
        }

        addresses.Sort();
        var output = new MemoryStream();
        var previous = textAddress;
        foreach (var address in addresses)
        {
            if (address != previous || output.Length == 0)
            {
                WriteUleb(output, address - previous);
                previous = address;
            }
        }

        output.WriteByte(0);
        return Padded(output);
    }

    public static void WriteUleb(Stream output, ulong value)
    {
        do
        {
            var digit = (byte)(value & 0x7f);
            value >>= 7;
            output.WriteByte(value != 0 ? (byte)(digit | 0x80) : digit);
        }
        while (value != 0);
    }

    public static void WriteSleb(Stream output, long value)
    {
        while (true)
        {
            var digit = (byte)(value & 0x7f);
            value >>= 7;
            var done = (value == 0 && (digit & 0x40) == 0) || (value == -1 && (digit & 0x40) != 0);
            output.WriteByte(done ? digit : (byte)(digit | 0x80));
            if (done)
            {
                return;
            }
        }
    }

    private static int UlebSize(ulong value)
    {
        var size = 1;
        while ((value >>= 7) != 0)
        {
            size++;
        }

        return size;
    }

    /// <summary>The tables are padded to pointer size, as the loader and the tools expect.</summary>
    private static byte[] Padded(MemoryStream output)
    {
        while (output.Length % PointerSize != 0)
        {
            output.WriteByte(0);
        }

        return output.ToArray();
    }

    private static void Insert(TrieNode root, string name, ulong offset)
    {
        var node = root;
        var rest = name;
        while (true)
        {
            var descended = false;
            for (var i = 0; i < node.Edges.Count; i++)
            {
                var (label, child) = node.Edges[i];
                var common = 0;
                while (common < label.Length && common < rest.Length && label[common] == rest[common])
                {
                    common++;
                }

                if (common == 0)
                {
                    continue;
                }

                if (common < label.Length)
                {
                    // The names part ways inside this edge: split it at the common prefix.
                    var middle = new TrieNode();
                    middle.Edges.Add((label[common..], child));
                    node.Edges[i] = (label[..common], middle);
                    child = middle;
                }

                node = child;
                rest = rest[common..];
                descended = true;
                break;
            }

            if (rest.Length == 0)
            {
                node.IsTerminal = true;
                node.ImageOffset = offset;
                return;
            }

            if (!descended)
            {
                node.Edges.Add((rest, new TrieNode { IsTerminal = true, ImageOffset = offset }));
                return;
            }
        }
    }

    private static void Flatten(TrieNode node, List<TrieNode> nodes)
    {
        nodes.Add(node);
        foreach (var (_, child) in node.Edges)
        {
            Flatten(child, nodes);
        }
    }

    private sealed class TrieNode
    {
        public List<(string Label, TrieNode Child)> Edges { get; } = [];

        public bool IsTerminal { get; set; }

        public ulong ImageOffset { get; set; }

        public int Offset { get; set; } = -1;

        // Terminal information: flags (0 for a regular symbol) and the offset of the symbol.
        private int TerminalSize => IsTerminal ? 1 + UlebSize(ImageOffset) : 0;

        public int Size()
        {
            var size = UlebSize((ulong)TerminalSize) + TerminalSize + 1;
            foreach (var (label, child) in Edges)
            {
                size += Encoding.UTF8.GetByteCount(label) + 1 + UlebSize((ulong)Math.Max(child.Offset, 0));
            }

            return size;
        }

        public void Write(Stream output)
        {
            WriteUleb(output, (ulong)TerminalSize);
            if (IsTerminal)
            {
                output.WriteByte(0);
                WriteUleb(output, ImageOffset);
            }

            output.WriteByte((byte)Edges.Count);
            foreach (var (label, child) in Edges)
            {
                output.Write(Encoding.UTF8.GetBytes(label));
                output.WriteByte(0);
                WriteUleb(output, (ulong)child.Offset);
            }
        }
    }
}
