using System.Buffers.Binary;
using System.Text;
using Ina.Linker.Input;

namespace Ina.Linker.Tests.Support;

/// <summary>Builds static libraries in the GNU <c>ar</c> format.</summary>
internal static class ArchiveBuilder
{
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
                var file = ObjectFile.Parse(members[i].Name, members[i].Data, static _ => true);
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
