using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Ina.Linker.Elf;

namespace Ina.Linker.Input;

/// <summary>A static library in the System V / GNU <c>ar</c> format.</summary>
internal sealed class ArchiveFile
{
    private const int HeaderSize = 60;
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

    public static ArchiveFile Parse(string name, ReadOnlyMemory<byte> data)
    {
        if (!IsArchive(data.Span))
        {
            throw new LinkerException($"{name}: not an archive");
        }

        var archive = new ArchiveFile(name, data);
        archive.ReadIndex();
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

        (memberName, memberData) = ReadMember(headerOffset);
        return true;
    }

    /// <summary>Enumerates every member that holds an object file, regardless of whether it was loaded.</summary>
    public IEnumerable<(string Name, ReadOnlyMemory<byte> Data)> Members()
    {
        var offset = Magic.Length;
        while (offset + HeaderSize <= _data.Length)
        {
            var (name, data) = ReadMember(offset);
            if (name is not ("/" or "//" or "/SYM64/"))
            {
                yield return (name, data);
            }

            offset = NextMemberOffset(offset, data.Length);
        }
    }

    private static int NextMemberOffset(int headerOffset, int size) => headerOffset + HeaderSize + size + (size & 1);

    private void ReadIndex()
    {
        var hasSymbolTable = false;
        var offset = Magic.Length;
        while (offset + HeaderSize <= _data.Length)
        {
            var (rawName, size) = ReadHeader(offset);
            var body = _data.Slice(offset + HeaderSize, size);
            if (rawName == "/")
            {
                ReadSymbolTable(body.Span, wide: false);
                hasSymbolTable = true;
            }
            else if (rawName == "/SYM64/")
            {
                ReadSymbolTable(body.Span, wide: true);
                hasSymbolTable = true;
            }
            else if (rawName == "//")
            {
                _longNames = body;
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
            BuildSymbolTableFromMembers(offset);
        }
    }

    private void ReadSymbolTable(ReadOnlySpan<byte> table, bool wide)
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

    /// <summary>Archives created without an index are indexed by reading the symbol table of every member.</summary>
    private void BuildSymbolTableFromMembers(int firstMemberOffset)
    {
        var offset = firstMemberOffset;
        while (offset + HeaderSize <= _data.Length)
        {
            var (name, data) = ReadMember(offset);
            if (ObjectFile.IsElf(data.Span))
            {
                var file = ObjectFile.Parse($"{Name}({name})", data, static _ => true);
                for (var i = file.FirstGlobalSymbol; i < file.ElfSymbols.Length; i++)
                {
                    var symbol = file.ElfSymbols[i];
                    if (!symbol.IsUndefined)
                    {
                        _symbolToMember.TryAdd(symbol.Name, offset);
                    }
                }
            }

            offset = NextMemberOffset(offset, data.Length);
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

    private (string Name, ReadOnlyMemory<byte> Data) ReadMember(int headerOffset)
    {
        var (rawName, size) = ReadHeader(headerOffset);
        var name = rawName;
        if (rawName.Length > 1 && rawName[0] == '/' && char.IsAsciiDigit(rawName[1]))
        {
            // "/<offset>" refers to the long name table, where names end with "/\n".
            var nameOffset = int.Parse(rawName.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture);
            var longNames = _longNames.Span;
            if (nameOffset < longNames.Length)
            {
                var rest = longNames[nameOffset..];
                var end = rest.IndexOf((byte)'\n');
                name = Encoding.UTF8.GetString(end < 0 ? rest : rest[..end]).TrimEnd('/');
            }
        }
        else if (rawName is not ("/" or "//" or "/SYM64/"))
        {
            name = rawName.TrimEnd('/');
        }

        return (name, _data.Slice(headerOffset + HeaderSize, size));
    }
}
