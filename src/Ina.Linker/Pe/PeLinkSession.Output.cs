using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Ina.Linker.Pe;

internal sealed partial class PeLinkSession
{
    /// <summary>The DOS header, its stub program and the signature occupy the first bytes of every image.</summary>
    private const int PeHeaderOffset = 0x80;

    private const uint ReadOnlyData = CoffSectionFlags.InitializedData | CoffSectionFlags.Read;

    private readonly List<PeOutputSection> _sections = [];
    private readonly List<PeImport> _usedImports = [];
    private List<string> _neededLibraries = [];

    private PeGeneratedChunk? _importDirectory;
    private PeGeneratedChunk? _importAddresses;
    private PeGeneratedChunk? _exportTable;
    private PeGeneratedChunk? _baseRelocations;
    private uint _headersSize;
    private uint _imageSize;
    private uint _fileSize;

    // ---------------------------------------------------------------------------------------------------------------
    // Import and export tables
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Creates the import tables for the imports that are actually used: the directory with one entry per DLL, the
    /// lookup and address tables with one slot per symbol, the names, and the thunks for direct calls.
    /// </summary>
    private void CreateImportTables()
    {
        _usedImports.AddRange(_imports.Values.Where(static i => i.IsUsed));
        if (_usedImports.Count == 0)
        {
            return;
        }

        // DLLs are listed in the canonical order of the import table, their symbols by name.
        var libraryOrder = _options.Imports.Libraries.Select(static (library, index) => (library, index)).ToDictionary(static l => l.library, static l => l.index);
        var libraries = _usedImports
            .GroupBy(static i => i.Library)
            .OrderBy(g => libraryOrder.GetValueOrDefault(g.Key, int.MaxValue))
            .Select(static g => (Name: g.Key, Imports: g.OrderBy(static i => i.Name, StringComparer.Ordinal).ToList()))
            .ToList();
        _neededLibraries = libraries.Select(static l => l.Name).ToList();

        var slotOffsets = new Dictionary<PeImport, uint>();
        var nameOffsets = new Dictionary<PeImport, uint>();
        var thunkOffsets = new Dictionary<PeImport, uint>();
        var libraryNameOffsets = new uint[libraries.Count];
        var firstSlots = new uint[libraries.Count];
        uint slots = 0, names = 0, thunks = 0;
        for (var i = 0; i < libraries.Count; i++)
        {
            firstSlots[i] = slots;
            foreach (var import in libraries[i].Imports)
            {
                slotOffsets[import] = slots;
                slots += 8;

                // A hint (unused, zero) followed by the name; entries start at even offsets.
                nameOffsets[import] = names;
                names += AlignUp(2 + (uint)Encoding.ASCII.GetByteCount(import.Name) + 1, 2);
                if (import.NeedsThunk)
                {
                    thunkOffsets[import] = thunks;
                    thunks += _target.ThunkSize;
                }
            }

            // Each table of a DLL ends with a null slot.
            slots += 8;
            libraryNameOffsets[i] = names;
            names += AlignUp((uint)Encoding.ASCII.GetByteCount(libraries[i].Name) + 1, 2);
        }

        PeGeneratedChunk lookup = null!, nameTable = null!;
        void WriteSlots(Span<byte> table)
        {
            foreach (var (import, offset) in slotOffsets)
            {
                LittleEndian.U64(table, (int)offset, nameTable.Rva + nameOffsets[import]);
            }
        }

        _importDirectory = new PeGeneratedChunk(".idata$2", ReadOnlyData, 4, (uint)(libraries.Count + 1) * 20, directory =>
        {
            for (var i = 0; i < libraries.Count; i++)
            {
                var entry = directory.Slice(i * 20, 20);
                LittleEndian.U32(entry, 0, lookup.Rva + firstSlots[i]);
                LittleEndian.U32(entry, 12, nameTable.Rva + libraryNameOffsets[i]);
                LittleEndian.U32(entry, 16, _importAddresses!.Rva + firstSlots[i]);
            }
        });

        // Both tables hold the same content in the file; the loader overwrites the address table with addresses.
        lookup = new PeGeneratedChunk(".idata$4", ReadOnlyData, 8, slots, WriteSlots);
        _importAddresses = new PeGeneratedChunk(".idata$5", ReadOnlyData, 8, slots, WriteSlots);
        nameTable = new PeGeneratedChunk(".idata$6", ReadOnlyData, 2, names, table =>
        {
            foreach (var (import, offset) in nameOffsets)
            {
                Encoding.ASCII.GetBytes(import.Name, table[((int)offset + 2)..]);
            }

            for (var i = 0; i < libraries.Count; i++)
            {
                Encoding.ASCII.GetBytes(libraries[i].Name, table[(int)libraryNameOffsets[i]..]);
            }
        });

        PeGeneratedChunk thunkTable = null!;
        thunkTable = new PeGeneratedChunk(".text$thunk", CoffSectionFlags.Code | CoffSectionFlags.Execute | CoffSectionFlags.Read, 16, thunks, code =>
        {
            foreach (var (import, offset) in thunkOffsets)
            {
                _target.WriteThunk(code.Slice((int)offset, (int)_target.ThunkSize), thunkTable.Rva + offset, _importAddresses.Rva + slotOffsets[import]);
            }
        });

        foreach (var chunk in new[] { _importDirectory, lookup, _importAddresses, nameTable, thunkTable })
        {
            chunk.IsLive = chunk.Size > 0;
            _generatedChunks.Add(chunk);
        }

        // From here on the import symbols are ordinary symbols inside the generated tables.
        foreach (var import in _usedImports)
        {
            if (import.AddressSymbol is { } address)
            {
                address.Kind = PeSymbolKind.Defined;
                address.Chunk = _importAddresses;
                address.Value = slotOffsets[import];
            }

            if (import is { NeedsThunk: true, ThunkSymbol: { } thunk })
            {
                thunk.Kind = PeSymbolKind.Defined;
                thunk.Chunk = thunkTable;
                thunk.Value = thunkOffsets[import];
            }
        }
    }

    /// <summary>Creates the export directory, which makes symbols of the executable visible to diagnostic tools.</summary>
    private void CreateExportTable()
    {
        if (_exportNames.Count == 0)
        {
            return;
        }

        var exports = new List<(string Name, PeSymbol Symbol)>();
        foreach (var name in _exportNames.Order(StringComparer.Ordinal))
        {
            var symbol = _globals[name].Target;
            if (symbol.Kind == PeSymbolKind.Defined)
            {
                exports.Add((name, symbol));
            }
            else if (!_reportedUndefined.Contains(symbol))
            {
                _errors.Add($"exported symbol {name} is not defined in the image");
            }
        }

        // IMAGE_EXPORT_DIRECTORY, then the addresses, the name pointers (sorted by name), the ordinals and the strings.
        const int DirectorySize = 40;
        var count = exports.Count;
        var moduleName = Encoding.ASCII.GetBytes(Path.GetFileName(_options.OutputPath) + '\0');
        var strings = (uint)(DirectorySize + count * 10);
        var size = strings + (uint)moduleName.Length + (uint)exports.Sum(static e => Encoding.ASCII.GetByteCount(e.Name) + 1);
        _exportTable = new PeGeneratedChunk(".edata", ReadOnlyData, 4, size, table =>
        {
            var rva = _exportTable!.Rva;
            LittleEndian.U32(table, 12, rva + strings);
            LittleEndian.U32(table, 16, 1);
            LittleEndian.U32(table, 20, (uint)count);
            LittleEndian.U32(table, 24, (uint)count);
            LittleEndian.U32(table, 28, rva + DirectorySize);
            LittleEndian.U32(table, 32, rva + DirectorySize + (uint)count * 4);
            LittleEndian.U32(table, 36, rva + DirectorySize + (uint)count * 8);

            moduleName.CopyTo(table[(int)strings..]);
            var nameOffset = (int)strings + moduleName.Length;
            for (var i = 0; i < count; i++)
            {
                LittleEndian.U32(table, DirectorySize + i * 4, exports[i].Symbol.Rva);
                LittleEndian.U32(table, DirectorySize + count * 4 + i * 4, rva + (uint)nameOffset);
                LittleEndian.U16(table, DirectorySize + count * 8 + i * 2, (ushort)i);
                nameOffset += Encoding.ASCII.GetBytes(exports[i].Name, table[nameOffset..]) + 1;
            }
        })
        {
            IsLive = true,
        };
        _generatedChunks.Add(_exportTable);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Layout
    // ---------------------------------------------------------------------------------------------------------------

    private static uint AlignUp(uint value, uint alignment) => (value + alignment - 1) & ~(alignment - 1);

    /// <summary>The section of the image that chunks with the given name end up in.</summary>
    private string OutputSectionName(string chunkName)
    {
        var dollar = chunkName.IndexOf('$');
        var name = dollar < 0 ? chunkName : chunkName[..dollar];

        // Merges can be chained (hydrated -> .bss -> .data); the bound guards against cycles.
        for (var depth = 0; depth < 8 && _merges.TryGetValue(name, out var target); depth++)
        {
            name = target;
        }

        return name;
    }

    /// <summary>
    /// Groups the live chunks into the sections of the image and gives everything its address. Within a section,
    /// chunks are ordered by their full names, which is how the parts of the initializer tables, the thread-local
    /// storage and the managed code line up between their start and end markers.
    /// </summary>
    private void AssignAddresses()
    {
        var byName = new Dictionary<string, PeOutputSection>(StringComparer.Ordinal);
        var groupRanks = new Dictionary<string, int>(StringComparer.Ordinal);
        var ordered = new List<(PeChunk Chunk, int GroupRank)>();
        foreach (var chunk in AllSections().Where(static s => s.IsLive).Concat<PeChunk>(_generatedChunks.Where(static c => c.IsLive)))
        {
            var name = OutputSectionName(chunk.SectionName);
            if (!byName.TryGetValue(name, out var section))
            {
                section = new PeOutputSection(name);
                byName.Add(name, section);
                _sections.Add(section);
            }

            chunk.Output = section;

            // Chunks that were merged in from a section of another name follow the section's own chunks, one
            // source section after the other.
            var dollar = chunk.SectionName.IndexOf('$');
            var ownName = dollar < 0 ? chunk.SectionName : chunk.SectionName[..dollar];
            var rank = 0;
            if (ownName != name && !groupRanks.TryGetValue(ownName, out rank))
            {
                rank = groupRanks.Count + 1;
                groupRanks.Add(ownName, rank);
            }

            ordered.Add((chunk, rank));
        }

        foreach (var (chunk, _) in ordered
            .OrderBy(static c => c.Chunk.Output!.Name != ".tls" && c.Chunk.IsUninitialized) // zero-initialized data needs no room in the file when it comes last
            .ThenBy(static c => c.GroupRank)
            .ThenBy(static c => c.Chunk.SectionName, StringComparer.Ordinal))
        {
            chunk.Output!.Chunks.Add(chunk);
        }

        _sections.RemoveAll(static s => s.Chunks.Sum(static c => (long)c.Size) == 0);
        var sections = _sections.OrderBy(Rank).ToList();
        _sections.Clear();
        _sections.AddRange(sections);

        var rva = PeConstants.SectionAlignment;
        foreach (var section in _sections)
        {
            PlaceSection(section, rva);
            rva = AlignUp(section.Rva + section.VirtualSize, PeConstants.SectionAlignment);
        }

        _imageSize = rva;
    }

    private void PlaceSection(PeOutputSection section, uint rva)
    {
        uint offset = 0, initializedEnd = 0, characteristics = 0;
        foreach (var chunk in section.Chunks)
        {
            offset = AlignUp(offset, chunk.Alignment);
            chunk.Rva = rva + offset;
            offset += chunk.Size;
            characteristics |= chunk.Characteristics;
            if (!chunk.IsUninitialized)
            {
                initializedEnd = offset;
            }
        }

        if ((characteristics & (CoffSectionFlags.Code | CoffSectionFlags.InitializedData)) != 0)
        {
            characteristics &= ~CoffSectionFlags.UninitializedData;
        }

        section.Rva = rva;
        section.VirtualSize = offset;
        section.RawSize = initializedEnd;
        section.Characteristics = characteristics;
        section.Index = _sections.IndexOf(section) + 1;
    }

    /// <summary>The conventional order of the sections: code, constants, data, unwind tables, resources, relocations.</summary>
    private static int Rank(PeOutputSection section)
    {
        var characteristics = section.Chunks.Aggregate(0u, static (flags, chunk) => flags | chunk.Characteristics);
        return section.Name switch
        {
            ".text" => 0,
            ".rdata" => 20,
            ".data" => 40,
            ".pdata" => 60,
            ".rsrc" => 80,
            ".reloc" => 90,
            _ when (characteristics & CoffSectionFlags.Execute) != 0 => 10,
            _ when (characteristics & CoffSectionFlags.Write) == 0 => 30,
            _ => 50,
        };
    }

    /// <summary>
    /// Lists the places that hold absolute addresses, which the loader adjusts when it moves the image. The list
    /// forms the last section, so it can be built when everything else has its address.
    /// </summary>
    private void CreateBaseRelocations()
    {
        var addresses = new List<uint>();
        if (_context.IsRelocatable)
        {
            foreach (var section in AllSections())
            {
                if (!section.IsLive)
                {
                    continue;
                }

                var symbols = section.File.Symbols;
                foreach (ref readonly var relocation in section.Relocations.AsSpan())
                {
                    if (relocation.Type == _target.Addr64Relocation && symbols[relocation.SymbolIndex].Target.Kind == PeSymbolKind.Defined)
                    {
                        addresses.Add(section.Rva + relocation.Offset);
                    }
                }
            }
        }

        if (addresses.Count > 0)
        {
            addresses.Sort();

            // One block per 4 KiB page: the page address, the block size, and a 16-bit entry per relocation with
            // the type in the upper four bits. Blocks are padded to a multiple of four bytes.
            var blocks = new MemoryStream();
            var header = new byte[8];
            var entry = new byte[2];
            for (var i = 0; i < addresses.Count;)
            {
                var page = addresses[i] & ~0xfffu;
                var end = i;
                while (end < addresses.Count && (addresses[end] & ~0xfffu) == page)
                {
                    end++;
                }

                var count = end - i;
                LittleEndian.U32(header, 0, page);
                LittleEndian.U32(header, 4, (uint)(8 + AlignUp((uint)count * 2, 4)));
                blocks.Write(header);
                for (; i < end; i++)
                {
                    LittleEndian.U16(entry, 0, (ushort)((PeConstants.BaseRelocationDir64 << 12) | (addresses[i] & 0xfff)));
                    blocks.Write(entry);
                }

                if (count % 2 != 0)
                {
                    LittleEndian.U16(entry, 0, PeConstants.BaseRelocationAbsolute);
                    blocks.Write(entry);
                }
            }

            var content = blocks.ToArray();
            _baseRelocations = new PeGeneratedChunk(
                ".reloc", ReadOnlyData | CoffSectionFlags.Discardable, 4, (uint)content.Length, destination => content.CopyTo(destination))
            {
                IsLive = true,
            };

            var section = new PeOutputSection(".reloc");
            section.Chunks.Add(_baseRelocations);
            _baseRelocations.Output = section;
            _sections.Add(section);
            PlaceSection(section, _imageSize);
            _imageSize = AlignUp(section.Rva + section.VirtualSize, PeConstants.SectionAlignment);
        }

        // The headers are followed by the content of the sections, each aligned in the file.
        _headersSize = AlignUp(
            (uint)(PeHeaderOffset + 4 + PeConstants.FileHeaderSize + PeConstants.OptionalHeaderSize + _sections.Count * PeConstants.SectionHeaderSize),
            PeConstants.FileAlignment);
        var fileOffset = _headersSize;
        foreach (var section in _sections)
        {
            section.FileOffset = section.RawSize > 0 ? fileOffset : 0;
            fileOffset += AlignUp(section.RawSize, PeConstants.FileAlignment);
        }

        _fileSize = fileOffset;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Writing
    // ---------------------------------------------------------------------------------------------------------------

    private LinkResult WriteOutput()
    {
        var image = new byte[_fileSize];
        foreach (var section in _sections)
        {
            foreach (var chunk in section.Chunks)
            {
                if (!chunk.IsUninitialized && chunk.Size > 0)
                {
                    chunk.Write(image.AsSpan((int)(section.FileOffset + chunk.Rva - section.Rva), (int)chunk.Size), _context);
                }
            }
        }

        var exceptionDirectory = SortExceptionDirectory(image);
        var tlsDirectory = FinishTlsDirectory(image);
        WriteHeaders(image, exceptionDirectory, tlsDirectory);

        // A time stamp derived from the content keeps the output reproducible.
        LittleEndian.U32(image, PeHeaderOffset + 8, LittleEndian.U32(SHA256.HashData(image), 0));

        var directory = Path.GetDirectoryName(Path.GetFullPath(_options.OutputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(_options.OutputPath, image);
        if (!OperatingSystem.IsWindows())
        {
            // Not needed by Windows, but it lets compatibility layers start the program from a Unix shell.
            File.SetUnixFileMode(
                _options.OutputPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        var totalSections = _objects.Sum(static o => o.Sections.Count(static s => s is not null));
        var liveSections = AllSections().Count(static s => s.IsLive);
        _logger.LogDebug(
            "Linked {Objects} objects, {Live} of {Total} sections, {Imports} imports from {Libraries} libraries",
            _objects.Count, liveSections, totalSections, _usedImports.Count, _neededLibraries.Count);

        return new LinkResult(
            _options.OutputPath,
            image.LongLength,
            _objects.Count,
            liveSections,
            totalSections - liveSections,
            _neededLibraries,
            _usedImports.Select(static i => i.Name).Order(StringComparer.Ordinal).ToList(),
            []);
    }

    /// <summary>Returns the part of the image buffer that holds the bytes at an address of an initialized section.</summary>
    private Span<byte> ImageBytes(byte[] image, uint rva, int length)
    {
        foreach (var section in _sections)
        {
            if (rva >= section.Rva && rva + length <= section.Rva + section.RawSize)
            {
                return image.AsSpan((int)(section.FileOffset + rva - section.Rva), length);
            }
        }

        throw new LinkerException($"address 0x{rva:x} is not inside the initialized part of a section");
    }

    /// <summary>
    /// Sorts the function table by address. Windows and the .NET runtime both look up the unwind information of
    /// a code address with a binary search over this table.
    /// </summary>
    private (uint Rva, uint Size) SortExceptionDirectory(byte[] image)
    {
        if (_sections.Find(static s => s.Name == ".pdata") is not { RawSize: > 0 } section)
        {
            return default;
        }

        var entrySize = (int)_target.RuntimeFunctionSize;
        var table = image.AsSpan((int)section.FileOffset, (int)section.RawSize);
        var entries = new List<byte[]>();
        for (var offset = 0; offset + entrySize <= table.Length; offset += entrySize)
        {
            var entry = table.Slice(offset, entrySize);

            // Padding between the tables of different objects is not an entry.
            if (entry.ContainsAnyExcept((byte)0))
            {
                entries.Add(entry.ToArray());
            }
        }

        entries.Sort(static (a, b) => LittleEndian.U32(a, 0).CompareTo(LittleEndian.U32(b, 0)));
        table.Clear();
        for (var i = 0; i < entries.Count; i++)
        {
            entries[i].CopyTo(table[(i * entrySize)..]);
        }

        return (section.Rva, (uint)(entries.Count * entrySize));
    }

    /// <summary>
    /// Records the alignment of the thread-local data in the TLS directory. The loader copies the initialization
    /// image into a block that it allocates per thread, and the variables keep their offsets only if the block is
    /// aligned like the image.
    /// </summary>
    private (uint Rva, uint Size) FinishTlsDirectory(byte[] image)
    {
        const int DirectorySize = 40;
        if (!_globals.TryGetValue(TlsDirectorySymbol, out var symbol) || symbol.Target is not { Kind: PeSymbolKind.Defined, Chunk.IsLive: true } directory)
        {
            return default;
        }

        var alignment = _sections.Find(static s => s.Name == ".tls")?.Chunks.Max(static c => c.Alignment) ?? 1;
        LittleEndian.U32(ImageBytes(image, directory.Rva, DirectorySize), 36, (uint)(BitOperations.Log2(alignment) + 1) << 20);
        return (directory.Rva, DirectorySize);
    }

    private void WriteHeaders(byte[] image, (uint Rva, uint Size) exceptionDirectory, (uint Rva, uint Size) tlsDirectory)
    {
        // DOS header and a stub for the case that the program is started on DOS.
        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        LittleEndian.U16(image, 0x02, 0x80);  // bytes in the last page
        LittleEndian.U16(image, 0x04, 1);     // pages
        LittleEndian.U16(image, 0x08, 4);     // header size in paragraphs
        LittleEndian.U16(image, 0x18, 0x40);  // offset of the relocation table
        LittleEndian.U32(image, 0x3c, PeHeaderOffset);
        ReadOnlySpan<byte> stub =
        [
            0x0e,             // push cs
            0x1f,             // pop  ds
            0xba, 0x0e, 0x00, // mov  dx, message
            0xb4, 0x09,       // mov  ah, 9        ; print string
            0xcd, 0x21,       // int  0x21
            0xb8, 0x01, 0x4c, // mov  ax, 0x4c01   ; exit with code 1
            0xcd, 0x21,       // int  0x21
        ];
        stub.CopyTo(image.AsSpan(0x40));
        "This program requires Windows.\r\n$"u8.CopyTo(image.AsSpan(0x40 + stub.Length));

        var header = image.AsSpan(PeHeaderOffset);
        "PE\0\0"u8.CopyTo(header);

        var hasRelocations = _baseRelocations is not null;
        var file = header[4..];
        LittleEndian.U16(file, 0, _target.Machine);
        LittleEndian.U16(file, 2, (ushort)_sections.Count);
        LittleEndian.U16(file, 16, PeConstants.OptionalHeaderSize);
        LittleEndian.U16(file, 18, (ushort)(
            PeConstants.FileExecutableImage | PeConstants.FileLargeAddressAware | (_context.IsRelocatable ? 0 : PeConstants.FileRelocsStripped)));

        uint SizeOf(uint content) =>
            (uint)_sections.Where(s => (s.Characteristics & content) != 0).Sum(static s => (long)AlignUp(s.RawSize, PeConstants.FileAlignment));

        var entryPoint = _globals[_options.EntryPoint].Target;
        var optional = file[PeConstants.FileHeaderSize..];
        LittleEndian.U16(optional, 0, PeConstants.Pe32PlusMagic);
        optional[2] = 14; // linker version, as reported by the tools that Ina stands in for
        LittleEndian.U32(optional, 4, SizeOf(CoffSectionFlags.Code));
        LittleEndian.U32(optional, 8, SizeOf(CoffSectionFlags.InitializedData));
        LittleEndian.U32(optional, 16, entryPoint.Rva);
        LittleEndian.U32(optional, 20, _sections.Find(static s => (s.Characteristics & CoffSectionFlags.Code) != 0)?.Rva ?? 0);
        LittleEndian.U64(optional, 24, _context.ImageBase);
        LittleEndian.U32(optional, 32, PeConstants.SectionAlignment);
        LittleEndian.U32(optional, 36, PeConstants.FileAlignment);
        LittleEndian.U16(optional, 40, 6); // operating system version 6.0
        LittleEndian.U16(optional, 48, 6); // subsystem version 6.0
        LittleEndian.U32(optional, 56, _imageSize);
        LittleEndian.U32(optional, 60, _headersSize);
        LittleEndian.U16(optional, 68, (ushort)_options.Subsystem);
        LittleEndian.U16(optional, 70, (ushort)(
            PeConstants.NxCompatible | PeConstants.TerminalServerAware
            | (_context.IsRelocatable && hasRelocations ? PeConstants.DynamicBase | PeConstants.HighEntropyVa : 0)));
        LittleEndian.U64(optional, 72, _options.StackReserve);
        LittleEndian.U64(optional, 80, 0x1000);    // stack commit
        LittleEndian.U64(optional, 88, 0x10_0000); // heap reserve
        LittleEndian.U64(optional, 96, 0x1000);    // heap commit
        LittleEndian.U32(optional, 108, PeConstants.DataDirectoryCount);

        const int Directories = PeHeaderOffset + 4 + PeConstants.FileHeaderSize + 112;
        void Directory(int index, uint rva, uint size)
        {
            if (size > 0)
            {
                LittleEndian.U32(image, Directories + index * 8, rva);
                LittleEndian.U32(image, Directories + index * 8 + 4, size);
            }
        }

        if (_exportTable is not null)
        {
            Directory(PeConstants.DirectoryExport, _exportTable.Rva, _exportTable.Size);
        }

        if (_importDirectory is not null)
        {
            Directory(PeConstants.DirectoryImport, _importDirectory.Rva, _importDirectory.Size);
            Directory(PeConstants.DirectoryImportAddressTable, _importAddresses!.Rva, _importAddresses.Size);
        }

        if (_sections.Find(static s => s.Name == ".rsrc") is { } resources)
        {
            Directory(PeConstants.DirectoryResource, resources.Rva, resources.VirtualSize);
        }

        Directory(PeConstants.DirectoryException, exceptionDirectory.Rva, exceptionDirectory.Size);
        if (_baseRelocations is not null)
        {
            Directory(PeConstants.DirectoryBaseRelocation, _baseRelocations.Rva, _baseRelocations.Size);
        }

        Directory(PeConstants.DirectoryTls, tlsDirectory.Rva, tlsDirectory.Size);
        if (_globals.TryGetValue(LoadConfigurationSymbol, out var symbol) && symbol.Target is { Kind: PeSymbolKind.Defined, Chunk.IsLive: true } configuration)
        {
            // The structure starts with its own size.
            Directory(PeConstants.DirectoryLoadConfig, configuration.Rva, LittleEndian.U32(ImageBytes(image, configuration.Rva, 4), 0));
        }

        var sectionTable = optional[PeConstants.OptionalHeaderSize..];
        for (var i = 0; i < _sections.Count; i++)
        {
            var section = _sections[i];
            var entry = sectionTable.Slice(i * PeConstants.SectionHeaderSize, PeConstants.SectionHeaderSize);
            var name = Encoding.ASCII.GetBytes(section.Name);
            name.AsSpan(0, Math.Min(name.Length, 8)).CopyTo(entry);
            LittleEndian.U32(entry, 8, section.VirtualSize);
            LittleEndian.U32(entry, 12, section.Rva);
            LittleEndian.U32(entry, 16, AlignUp(section.RawSize, PeConstants.FileAlignment));
            LittleEndian.U32(entry, 20, section.FileOffset);
            LittleEndian.U32(entry, 36, section.Characteristics & CoffSectionFlags.ImageMask);
        }
    }
}
