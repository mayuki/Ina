using System.Buffers.Binary;
using System.Text;
using Ina.Linker.MachO;

namespace Ina.Linker.Tests.Support;

/// <summary>Builds static libraries in the BSD <c>ar</c> format that Apple's tools produce.</summary>
internal static class MachArchiveBuilder
{
    private const int HeaderSize = 60;

    public static byte[] Build(bool withSymbolTable, params (string Name, byte[] Data)[] members)
    {
        // Every member stores its name in front of its content, padded to a multiple of eight bytes.
        static int NameSize(string name) => (Encoding.UTF8.GetByteCount(name) + 8) & ~7;

        var symbols = new List<(string Name, int Member)>();
        if (withSymbolTable)
        {
            for (var i = 0; i < members.Length; i++)
            {
                foreach (var symbol in MachObjectFile.Parse(members[i].Name, members[i].Data).Nlist)
                {
                    if (symbol.IsExternal && !symbol.IsUndefined && !symbol.IsCommon)
                    {
                        symbols.Add((symbol.Name, i));
                    }
                }
            }
        }

        const string tableName = "__.SYMDEF";
        var strings = new MemoryStream();
        var nameOffsets = new int[symbols.Count];
        for (var i = 0; i < symbols.Count; i++)
        {
            nameOffsets[i] = (int)strings.Length;
            strings.Write(Encoding.UTF8.GetBytes(symbols[i].Name));
            strings.WriteByte(0);
        }

        while (strings.Length % 8 != 0)
        {
            strings.WriteByte(0);
        }

        var tableSize = 4 + symbols.Count * 8 + 4 + (int)strings.Length;
        var offset = 8;
        if (withSymbolTable)
        {
            offset += HeaderSize + NameSize(tableName) + tableSize;
        }

        var memberOffsets = new int[members.Length];
        for (var i = 0; i < members.Length; i++)
        {
            memberOffsets[i] = offset;
            var size = NameSize(members[i].Name) + members[i].Data.Length;
            offset += HeaderSize + size + (size & 1);
        }

        var archive = new MemoryStream();
        archive.Write("!<arch>\n"u8);

        void WriteMember(string name, ReadOnlySpan<byte> data)
        {
            var nameSize = NameSize(name);
            var size = nameSize + data.Length;
            archive.Write(Encoding.ASCII.GetBytes($"{"#1/" + nameSize,-16}{"0",-12}{"0",-6}{"0",-6}{"644",-8}{size,-10}`\n"));
            var paddedName = new byte[nameSize];
            Encoding.UTF8.GetBytes(name, paddedName);
            archive.Write(paddedName);
            archive.Write(data);
            if ((size & 1) != 0)
            {
                archive.WriteByte((byte)'\n');
            }
        }

        if (withSymbolTable)
        {
            // The index: the size of the entry array, pairs of (name offset, member offset), the size of the names, the names.
            var table = new byte[tableSize];
            BinaryPrimitives.WriteUInt32LittleEndian(table, (uint)(symbols.Count * 8));
            for (var i = 0; i < symbols.Count; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(4 + i * 8), (uint)nameOffsets[i]);
                BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(8 + i * 8), (uint)memberOffsets[symbols[i].Member]);
            }

            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(4 + symbols.Count * 8), (uint)strings.Length);
            strings.ToArray().CopyTo(table, 8 + symbols.Count * 8);
            WriteMember(tableName, table);
        }

        foreach (var (name, data) in members)
        {
            WriteMember(name, data);
        }

        return archive.ToArray();
    }
}
