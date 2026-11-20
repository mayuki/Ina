using System.Buffers.Binary;
using System.Text;
using Ina.Linker.Elf;
using Ina.Linker.Pe;

namespace Ina.Linker.Tests.Support;

/// <summary>Builds static libraries in the <c>ar</c> format, in the flavors of GNU tools and of the Microsoft librarian.</summary>
internal static class ArchiveBuilder
{
    /// <summary>
    /// Builds a library of COFF objects the way <c>lib.exe</c> does: with the two symbol indexes ("linker members")
    /// and with member names that are stored NUL-terminated in the long name table.
    /// </summary>
    /// <param name="members">
    /// The objects; a member that is not a COFF object is indexed under the symbols given in its name after a
    /// colon ("name:symbol,symbol").
    /// </param>
    public static byte[] BuildMicrosoft(params (string Name, byte[] Data)[] members)
    {
        var longNames = new MemoryStream();
        var headerNames = new string[members.Length];
        var symbols = new List<(string Name, int Member)>();
        for (var i = 0; i < members.Length; i++)
        {
            var parts = members[i].Name.Split(':');
            headerNames[i] = "/" + longNames.Length;
            longNames.Write(Encoding.ASCII.GetBytes(parts[0] + "\0"));
            if (parts.Length > 1)
            {
                symbols.AddRange(parts[1].Split(',').Select(name => (name, i)));
            }
            else
            {
                var file = CoffObject.Parse(parts[0], members[i].Data);
                symbols.AddRange(file.Records
                    .Where(static r => r is { IsAuxiliary: false, IsExternal: true } && (r.SectionNumber != 0 || r.IsCommon))
                    .Select(r => (r.Name, i)));
            }
        }

        static int Padded(int size) => size + (size & 1);

        var names = symbols.Sum(static s => Encoding.UTF8.GetByteCount(s.Name) + 1);
        var firstSize = 4 + symbols.Count * 4 + names;
        var secondSize = 4 + members.Length * 4 + 4 + symbols.Count * 2 + names;
        var offset = 8 + 60 + Padded(firstSize) + 60 + Padded(secondSize) + 60 + Padded((int)longNames.Length);
        var memberOffsets = new int[members.Length];
        for (var i = 0; i < members.Length; i++)
        {
            memberOffsets[i] = offset;
            offset += 60 + Padded(members[i].Data.Length);
        }

        var archive = new MemoryStream();
        archive.Write("!<arch>\n"u8);

        void WriteMember(string name, ReadOnlySpan<byte> data)
        {
            archive.Write(Encoding.ASCII.GetBytes($"{name,-16}{"0",-12}{"",-6}{"",-6}{"0",-8}{data.Length,-10}`\n"));
            archive.Write(data);
            if ((data.Length & 1) != 0)
            {
                archive.WriteByte((byte)'\n');
            }
        }

        // First linker member: big-endian, symbols in member order. This is the index that all tools understand.
        var first = new byte[firstSize];
        BinaryPrimitives.WriteUInt32BigEndian(first, (uint)symbols.Count);
        var nameOffset = 4 + symbols.Count * 4;
        for (var i = 0; i < symbols.Count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(first.AsSpan(4 + i * 4), (uint)memberOffsets[symbols[i].Member]);
            nameOffset += Encoding.UTF8.GetBytes(symbols[i].Name, first.AsSpan(nameOffset)) + 1;
        }

        WriteMember("/", first);

        // Second linker member: little-endian, a table of member offsets and symbols sorted by name.
        var second = new byte[secondSize];
        BinaryPrimitives.WriteUInt32LittleEndian(second, (uint)members.Length);
        for (var i = 0; i < members.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(second.AsSpan(4 + i * 4), (uint)memberOffsets[i]);
        }

        var sorted = symbols.OrderBy(static s => s.Name, StringComparer.Ordinal).ToList();
        var position = 4 + members.Length * 4;
        BinaryPrimitives.WriteUInt32LittleEndian(second.AsSpan(position), (uint)sorted.Count);
        position += 4;
        foreach (var symbol in sorted)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(second.AsSpan(position), (ushort)(symbol.Member + 1));
            position += 2;
        }

        foreach (var symbol in sorted)
        {
            position += Encoding.UTF8.GetBytes(symbol.Name, second.AsSpan(position)) + 1;
        }

        WriteMember("/", second);
        WriteMember("//", longNames.ToArray());
        for (var i = 0; i < members.Length; i++)
        {
            WriteMember(headerNames[i], members[i].Data);
        }

        return archive.ToArray();
    }

    public static byte[] Build(bool withSymbolTable, params (string Name, byte[] Data)[] members)
    {
        // Names longer than 15 characters go into the long name table and are referenced as "/<offset>".
        var longNames = new MemoryStream();
        var headerNames = new string[members.Length];
        for (var i = 0; i < members.Length; i++)
        {
            if (members[i].Name.Length > 15)
            {
                headerNames[i] = "/" + longNames.Length;
                longNames.Write(Encoding.ASCII.GetBytes(members[i].Name + "/\n"));
            }
            else
            {
                headerNames[i] = members[i].Name + "/";
            }
        }

        var symbols = new List<(string Name, int Member)>();
        if (withSymbolTable)
        {
            for (var i = 0; i < members.Length; i++)
            {
                var file = ElfObjectFile.Parse(members[i].Name, members[i].Data, static _ => true);
                for (var s = file.FirstGlobalSymbol; s < file.ElfSymbols.Length; s++)
                {
                    if (!file.ElfSymbols[s].IsUndefined)
                    {
                        symbols.Add((file.ElfSymbols[s].Name, i));
                    }
                }
            }
        }

        static int Padded(int size) => size + (size & 1);

        var symbolTableSize = 4 + symbols.Count * 4 + symbols.Sum(static s => Encoding.UTF8.GetByteCount(s.Name) + 1);
        var offset = 8;
        if (withSymbolTable)
        {
            offset += 60 + Padded(symbolTableSize);
        }

        if (longNames.Length > 0)
        {
            offset += 60 + Padded((int)longNames.Length);
        }

        var memberOffsets = new int[members.Length];
        for (var i = 0; i < members.Length; i++)
        {
            memberOffsets[i] = offset;
            offset += 60 + Padded(members[i].Data.Length);
        }

        var archive = new MemoryStream();
        archive.Write("!<arch>\n"u8);

        void WriteMember(string name, ReadOnlySpan<byte> data)
        {
            var header = $"{name,-16}{"0",-12}{"0",-6}{"0",-6}{"644",-8}{data.Length,-10}`\n";
            archive.Write(Encoding.ASCII.GetBytes(header));
            archive.Write(data);
            if ((data.Length & 1) != 0)
            {
                archive.WriteByte((byte)'\n');
            }
        }

        if (withSymbolTable)
        {
            var table = new byte[symbolTableSize];
            BinaryPrimitives.WriteUInt32BigEndian(table, (uint)symbols.Count);
            var nameOffset = 4 + symbols.Count * 4;
            for (var i = 0; i < symbols.Count; i++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(4 + i * 4), (uint)memberOffsets[symbols[i].Member]);
                nameOffset += Encoding.UTF8.GetBytes(symbols[i].Name, table.AsSpan(nameOffset)) + 1;
            }

            WriteMember("/", table);
        }

        if (longNames.Length > 0)
        {
            WriteMember("//", longNames.ToArray());
        }

        for (var i = 0; i < members.Length; i++)
        {
            WriteMember(headerNames[i], members[i].Data);
        }

        return archive.ToArray();
    }
}
