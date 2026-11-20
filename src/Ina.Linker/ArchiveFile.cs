using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Ina.Linker;

/// <summary>
/// A static library in the <c>ar</c> format, in any of its dialects: System V / GNU (used for ELF), BSD (used by
/// Apple's tools for Mach-O) and the one of the Microsoft librarian (used for COFF). They differ in how long member
/// names and the symbol index are stored.
/// </summary>
internal sealed class ArchiveFile
{
    private const int HeaderSize = 60;
    private const string BsdNamePrefix = "#1/";
    private static readonly byte[] Magic = "!<arch>\n"u8.ToArray();

    private readonly ReadOnlyMemory<byte> _data;
    private readonly Dictionary<string, int> _symbolToMember = new(StringComparer.Ordinal);
    private readonly HashSet<int> _loadedMembers = [];
    private ReadOnlyMemory<byte> _longNames;

    private ArchiveFile(string name, ReadOnlyMemory<byte> data)
    {
        Name = name;
        _data = data;
    }

    public string Name { get; }

    public static bool IsArchive(ReadOnlySpan<byte> data) => data.StartsWith(Magic);

    /// <summary>Parses an archive.</summary>
    /// <param name="definedSymbols">
    /// Returns the names of the external symbols that a member defines, or null when the member is not an object
    /// file. Only used for archives that were created without a symbol index.
    /// </param>
    public static ArchiveFile Parse(string name, ReadOnlyMemory<byte> data, Func<string, ReadOnlyMemory<byte>, IEnumerable<string>?> definedSymbols)
    {
        if (!IsArchive(data.Span))
        {
            throw new LinkerException($"{name}: not an archive");
        }

        var archive = new ArchiveFile(name, data);
        archive.ReadIndex(definedSymbols);
        return archive;
    }

    /// <summary>
    /// Finds the not yet loaded member that defines <paramref name="symbol"/> and marks it as loaded.
    /// </summary>
    public bool TryTakeMember(string symbol, out string memberName, out ReadOnlyMemory<byte> memberData)
    {
        memberName = string.Empty;
        memberData = default;
        if (!_symbolToMember.TryGetValue(symbol, out var headerOffset) || !_loadedMembers.Add(headerOffset))
        {
            return false;
        }

        (memberName, memberData, _) = ReadMember(headerOffset);
        return true;
    }

    /// <summary>Enumerates every member that holds an object file, regardless of whether it was loaded.</summary>
    public IEnumerable<(string Name, ReadOnlyMemory<byte> Data)> Members()
    {
        var offset = Magic.Length;
        while (offset + HeaderSize <= _data.Length)
        {
            var (name, data, size) = ReadMember(offset);
            if (!IsSpecialMember(name))
            {
                yield return (name, data);
            }

            offset = NextMemberOffset(offset, size);
        }
    }

    /// <summary>The symbol index and the table of long names are members with reserved names.</summary>
    private static bool IsSpecialMember(string name) =>
        name is "/" or "//" or "/SYM64/" || name.StartsWith("__.SYMDEF", StringComparison.Ordinal);

    private static int NextMemberOffset(int headerOffset, int size) => headerOffset + HeaderSize + size + (size & 1);

    private void ReadIndex(Func<string, ReadOnlyMemory<byte>, IEnumerable<string>?> definedSymbols)
    {
        var hasSymbolTable = false;
        var offset = Magic.Length;
        while (offset + HeaderSize <= _data.Length)
        {
            var (name, body, size) = ReadMember(offset);
            if (name == "/")
            {
                // Microsoft libraries have a second index of the same name in a different layout; the first one
                // has the format that all tools share.
                if (!hasSymbolTable)
                {
                    ReadGnuSymbolTable(body.Span, wide: false);
                    hasSymbolTable = true;
                }
            }
            else if (name == "/SYM64/")
            {
                ReadGnuSymbolTable(body.Span, wide: true);
                hasSymbolTable = true;
            }
            else if (name == "//")
            {
                _longNames = body;
            }
            else if (name is "__.SYMDEF" or "__.SYMDEF SORTED")
            {
                ReadBsdSymbolTable(body.Span, wide: false);
                hasSymbolTable = true;
            }
            else if (name is "__.SYMDEF_64" or "__.SYMDEF_64 SORTED")
            {
                ReadBsdSymbolTable(body.Span, wide: true);
                hasSymbolTable = true;
            }
            else
            {
                // The special members always precede the regular ones.
                break;
            }

            offset = NextMemberOffset(offset, size);
        }

        if (!hasSymbolTable)
        {
            BuildSymbolTableFromMembers(offset, definedSymbols);
        }
    }

    /// <summary>GNU index: a big-endian count, the member offsets, then the names in the same order.</summary>
    private void ReadGnuSymbolTable(ReadOnlySpan<byte> table, bool wide)
    {
        var entrySize = wide ? 8 : 4;
        if (table.Length < entrySize)
        {
            return;
        }

        var count = wide ? checked((int)BinaryPrimitives.ReadUInt64BigEndian(table)) : checked((int)BinaryPrimitives.ReadUInt32BigEndian(table));
        var names = table[(entrySize * (count + 1))..];
        var nameOffset = 0;
        for (var i = 0; i < count; i++)
        {
            var entry = table[(entrySize * (i + 1))..];
            var memberOffset = wide ? checked((int)BinaryPrimitives.ReadUInt64BigEndian(entry)) : checked((int)BinaryPrimitives.ReadUInt32BigEndian(entry));
            var length = names[nameOffset..].IndexOf((byte)0);
            if (length < 0)
            {
                break;
            }

            // The first member defining a symbol wins, as with any other linker.
            _symbolToMember.TryAdd(Encoding.UTF8.GetString(names.Slice(nameOffset, length)), memberOffset);
            nameOffset += length + 1;
        }
    }

    /// <summary>
    /// BSD index: the size of an array of (name offset, member offset) pairs, the array, the size of the string
    /// table and the strings; all little-endian for the targets Ina supports.
    /// </summary>
    private void ReadBsdSymbolTable(ReadOnlySpan<byte> table, bool wide)
    {
        var wordSize = wide ? 8 : 4;
        long Word(ReadOnlySpan<byte> data, int position) =>
            wide ? checked((long)BinaryPrimitives.ReadUInt64LittleEndian(data[position..])) : BinaryPrimitives.ReadUInt32LittleEndian(data[position..]);

        if (table.Length < wordSize)
        {
            return;
        }

        var arraySize = Word(table, 0);
        if (arraySize < 0 || wordSize + arraySize + wordSize > table.Length)
        {
            throw new LinkerException($"{Name}: malformed archive symbol table");
        }

        var count = (int)(arraySize / (wordSize * 2));
        var strings = table[(int)(wordSize + arraySize + wordSize)..];
        for (var i = 0; i < count; i++)
        {
            var entry = wordSize + i * wordSize * 2;
            var nameOffset = Word(table, entry);
            var memberOffset = Word(table, entry + wordSize);
            if (nameOffset < 0 || nameOffset >= strings.Length)
            {
                continue;
            }

            var name = strings[(int)nameOffset..];
            var length = name.IndexOf((byte)0);
            _symbolToMember.TryAdd(Encoding.UTF8.GetString(length < 0 ? name : name[..length]), checked((int)memberOffset));
        }
    }

    /// <summary>Archives created without an index are indexed by reading the symbol table of every member.</summary>
    private void BuildSymbolTableFromMembers(int firstMemberOffset, Func<string, ReadOnlyMemory<byte>, IEnumerable<string>?> definedSymbols)
    {
        var offset = firstMemberOffset;
        while (offset + HeaderSize <= _data.Length)
        {
            var (name, data, size) = ReadMember(offset);
            if (definedSymbols($"{Name}({name})", data) is { } symbols)
            {
                foreach (var symbol in symbols)
                {
                    _symbolToMember.TryAdd(symbol, offset);
                }
            }

            offset = NextMemberOffset(offset, size);
        }
    }

    private (string RawName, int Size) ReadHeader(int offset)
    {
        var header = _data.Span.Slice(offset, HeaderSize);
        if (header[58] != (byte)'`' || header[59] != (byte)'\n')
        {
            throw new LinkerException($"{Name}: malformed archive member header at offset 0x{offset:x}");
        }

        var rawName = Encoding.ASCII.GetString(header[..16]).TrimEnd(' ');
        var sizeText = Encoding.ASCII.GetString(header.Slice(48, 10)).Trim();
        if (!int.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var size) || offset + HeaderSize + size > _data.Length)
        {
            throw new LinkerException($"{Name}: malformed archive member size at offset 0x{offset:x}");
        }

        return (rawName, size);
    }

    /// <summary>Reads a member. <c>Size</c> is the size recorded in the header, which is what the next member follows.</summary>
    private (string Name, ReadOnlyMemory<byte> Data, int Size) ReadMember(int headerOffset)
    {
        var (rawName, size) = ReadHeader(headerOffset);
        var data = _data.Slice(headerOffset + HeaderSize, size);
        var name = rawName;
        if (rawName.StartsWith(BsdNamePrefix, StringComparison.Ordinal))
        {
            // BSD: "#1/<length>" says that the name occupies the first <length> bytes of the member, padded with NULs.
            if (!int.TryParse(rawName.AsSpan(BsdNamePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var nameLength) || nameLength > size)
            {
                throw new LinkerException($"{Name}: malformed archive member name at offset 0x{headerOffset:x}");
            }

            name = Encoding.UTF8.GetString(data.Span[..nameLength]).TrimEnd('\0');
            data = data[nameLength..];
        }
        else if (rawName.Length > 1 && rawName[0] == '/' && char.IsAsciiDigit(rawName[1]))
        {
            // GNU and Microsoft: "/<offset>" refers to the long name table, where names end with "/\n" (GNU) or
            // a NUL (Microsoft).
            var nameOffset = int.Parse(rawName.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture);
            var longNames = _longNames.Span;
            if (nameOffset < longNames.Length)
            {
                var rest = longNames[nameOffset..];
                var end = rest.IndexOfAny((byte)'\n', (byte)0);
                name = Encoding.UTF8.GetString(end < 0 ? rest : rest[..end]).TrimEnd('/');
            }
        }
        else if (rawName is not ("/" or "//" or "/SYM64/"))
        {
            name = rawName.TrimEnd('/');
        }

        return (name, data, size);
    }
}
