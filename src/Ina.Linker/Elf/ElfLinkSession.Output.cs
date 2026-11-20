using System.Text;
using Microsoft.Extensions.Logging;

namespace Ina.Linker.Elf;

internal sealed partial class ElfLinkSession
{
    private const int ElfHeaderSize = 64;
    private const int ProgramHeaderSize = 56;
    private const int SectionHeaderSize = 64;

    // Chunks are ordered by rank. The hundreds digit selects the segment: 0/1 read-only, 2 executable, 3 read-only
    // after relocation (RELRO), 4 writable data, 5 zero-initialized data.
    private const int RankReadOnly = 100;
    private const int RankExecutable = 200;
    private const int RankRelRo = 300;
    private const int RankData = 400;
    private const int RankBss = 500;

    private readonly Dictionary<string, OutputSection> _outputSections = new(StringComparer.Ordinal);
    private readonly List<(int Rank, OutputChunk Chunk)> _rankedChunks = [];
    private readonly List<OutputChunk> _chunks = [];
    private readonly List<OutputChunk> _nonAllocChunks = [];
    private readonly List<Segment> _segments = [];

    private StringTableChunk _dynamicStrings = null!;
    private DynamicSymbolTable _dynamicSymbols = null!;
    private VersionTables _versions = null!;
    private DynamicChunk _dynamic = null!;
    private EhFrameChunk _ehFrame = null!;
    private EhFrameHeaderChunk _ehFrameHeader = null!;
    private OutputChunk? _interp;
    private StringTableChunk _sectionNames = null!;
    private List<string> _neededLibraries = [];

    private int _programHeaderCount;
    private ulong _relRoStart;
    private ulong _relRoEnd;
    private ulong _textEnd;
    private ulong _dataEnd;
    private ulong _imageEnd;
    private ulong _fileSize;

    private sealed record Segment(uint Type, uint Flags, ulong Offset, ulong Address, ulong FileSize, ulong MemorySize, ulong Alignment);

    // ---------------------------------------------------------------------------------------------------------------
    // Output sections
    // ---------------------------------------------------------------------------------------------------------------

    private void CreateOutputSections()
    {
        foreach (var section in AllSections())
        {
            if (!section.IsLive)
            {
                continue;
            }

            var name = SectionNaming.OutputName(section.Name);
            if (!_outputSections.TryGetValue(name, out var output))
            {
                var type = section.Type == SectionType.X86_64Unwind ? SectionType.ProgBits : section.Type;
                output = new OutputSection(name, type, section.Flags & (SectionFlags.Alloc | SectionFlags.Write | SectionFlags.ExecInstr | SectionFlags.Tls));
                _outputSections.Add(name, output);
            }

            output.Add(section);
        }

        foreach (var output in _outputSections.Values)
        {
            if (output.Name is ".init_array" or ".fini_array" or ".preinit_array")
            {
                // Constructors run in priority order; the sort is stable so equal priorities keep their input order.
                var ordered = output.Members.OrderBy(static m => SectionNaming.InitPriority(m.Name)).ToList();
                output.Members.Clear();
                output.Members.AddRange(ordered);
            }

            output.Finish();
            _rankedChunks.Add((RankOf(output), output));
        }

        _ehFrame = new EhFrameChunk();
        foreach (var file in _objects)
        {
            foreach (var record in file.EhFrameRecords)
            {
                if (record.IsLive)
                {
                    _ehFrame.Add(record);
                }
            }
        }

        _ehFrameHeader = new EhFrameHeaderChunk(_ehFrame);
        _ehFrameHeader.Finish();
        if (_ehFrame.Size > 0)
        {
            _rankedChunks.Add((RankReadOnly + 50, _ehFrameHeader));
            _rankedChunks.Add((RankReadOnly + 51, _ehFrame));
        }
    }

    private static int RankOf(OutputSection section)
    {
        if (section.IsExecutable)
        {
            return RankExecutable + (section.Name == ".text" ? 0 : 1);
        }

        if (!section.IsWritable)
        {
            return RankReadOnly + (section.Name == ".rodata" ? 10 : 11);
        }

        if (section.IsTls)
        {
            return RankRelRo + (section.IsNoBits ? 1 : 0);
        }

        return section.Name switch
        {
            ".preinit_array" => RankRelRo + 10,
            ".init_array" => RankRelRo + 11,
            ".fini_array" => RankRelRo + 12,
            ".ctors" => RankRelRo + 13,
            ".dtors" => RankRelRo + 14,
            ".data.rel.ro" => RankRelRo + 20,
            ".data" => RankData,
            ".bss" => RankBss + 1,
            _ => section.IsNoBits ? RankBss : RankData + 1,
        };
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Dynamic linking information
    // ---------------------------------------------------------------------------------------------------------------

    private void FinishDynamicSections()
    {
        _dynamicStrings = new StringTableChunk(".dynstr", SectionFlags.Alloc);
        _dynamicSymbols = new DynamicSymbolTable(_dynamicStrings);
        foreach (var symbol in _imports)
        {
            _dynamicSymbols.Add(symbol);
        }

        foreach (var symbol in _exports)
        {
            _dynamicSymbols.Add(symbol);
        }

        // Libraries are listed in the canonical order of the import table, restricted to those actually used.
        var used = _imports.Select(static s => s.Import!.Library).ToHashSet(StringComparer.Ordinal);
        _neededLibraries = _options.Imports.Libraries.Where(used.Contains).ToList();

        _versions = new VersionTables(_dynamicSymbols, _dynamicStrings);
        _versions.Finish();
        var hash = new HashChunk(_dynamicSymbols);
        hash.Finish();
        var relocations = new RelaDynChunk(_dynamicSymbols);
        relocations.Finish(_context.DynamicRelocations, _target.RelativeRelocation);

        _dynamic = new DynamicChunk(_dynamicStrings);
        foreach (var library in _neededLibraries)
        {
            _dynamic.Add(DynamicTag.Needed, _dynamicStrings.Add(library));
        }

        _dynamic.Add(DynamicTag.Hash, () => hash.Address);
        _dynamic.Add(DynamicTag.StrTab, () => _dynamicStrings.Address);
        _dynamic.Add(DynamicTag.SymTab, () => _dynamicSymbols.Address);
        _dynamic.Add(DynamicTag.StrSz, () => _dynamicStrings.Size);
        _dynamic.Add(DynamicTag.SymEnt, 24);
        if (relocations.Size > 0)
        {
            _dynamic.Add(DynamicTag.Rela, () => relocations.Address);
            _dynamic.Add(DynamicTag.RelaSz, relocations.Size);
            _dynamic.Add(DynamicTag.RelaEnt, 24);
            if (relocations.RelativeCount > 0)
            {
                _dynamic.Add(DynamicTag.RelaCount, (ulong)relocations.RelativeCount);
            }
        }

        AddArrayTags(".preinit_array", DynamicTag.PreInitArray, DynamicTag.PreInitArraySz);
        AddArrayTags(".init_array", DynamicTag.InitArray, DynamicTag.InitArraySz);
        AddArrayTags(".fini_array", DynamicTag.FiniArray, DynamicTag.FiniArraySz);

        if (_versions.NeedCount > 0)
        {
            _dynamic.Add(DynamicTag.VerNeed, () => _versions.VersionNeeds.Address);
            _dynamic.Add(DynamicTag.VerNeedNum, (ulong)_versions.NeedCount);
            _dynamic.Add(DynamicTag.VerSym, () => _versions.VersionSymbols.Address);
        }

        // All imports are bound through the GOT at startup; there is no lazy binding to defer.
        _dynamic.Add(DynamicTag.Flags, DynamicTag.FlagBindNow);
        _dynamic.Add(DynamicTag.Flags1, DynamicTag.Flag1Now | (_context.IsPositionIndependent ? DynamicTag.Flag1Pie : 0));

        // Debuggers find the list of loaded libraries through this entry, which the loader fills in.
        _dynamic.Add(DynamicTag.Debug, 0);

        _interp = new DataChunk(".interp", SectionType.ProgBits, SectionFlags.Alloc, 1, Encoding.UTF8.GetBytes(_options.Imports.Interpreter + '\0'));
        _rankedChunks.Add((10, _interp));
        _rankedChunks.Add((20, hash));
        _rankedChunks.Add((21, _dynamicSymbols));
        _rankedChunks.Add((22, _dynamicStrings));
        if (_versions.NeedCount > 0)
        {
            _rankedChunks.Add((23, _versions.VersionSymbols));
            _rankedChunks.Add((24, _versions.VersionNeeds));
        }

        if (relocations.Size > 0)
        {
            _rankedChunks.Add((25, relocations));
        }

        if (_context.Plt.Size > 0)
        {
            _rankedChunks.Add((RankExecutable + 50, _context.Plt));
        }

        _rankedChunks.Add((RankRelRo + 30, _dynamic));
        if (_context.Got.Size > 0)
        {
            _rankedChunks.Add((RankRelRo + 31, _context.Got));
        }
    }

    private void AddArrayTags(string sectionName, long addressTag, long sizeTag)
    {
        if (_outputSections.TryGetValue(sectionName, out var section) && section.Size > 0)
        {
            _dynamic.Add(addressTag, () => section.Address);
            _dynamic.Add(sizeTag, section.Size);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Layout
    // ---------------------------------------------------------------------------------------------------------------

    private static ulong AlignUp(ulong value, ulong alignment) => (value + alignment - 1) & ~(alignment - 1);

    private void AssignAddresses()
    {
        // OrderBy is stable: chunks of equal rank keep the order in which their first input section appeared.
        var ordered = _rankedChunks.Where(static c => c.Chunk.Size > 0).OrderBy(static c => c.Rank).ToList();
        var tls = ordered.Where(static c => c.Chunk.IsTls).Select(static c => c.Chunk).ToList();
        var hasEhFrameHeader = ordered.Any(c => c.Chunk == _ehFrameHeader);

        // PT_PHDR, PT_INTERP, three PT_LOAD, PT_DYNAMIC, PT_GNU_STACK, PT_GNU_RELRO, plus the optional ones.
        _programHeaderCount = 8 + (tls.Count > 0 ? 1 : 0) + (hasEhFrameHeader ? 1 : 0);

        var pageSize = _target.PageSize;
        var imageBase = _context.ImageBase;
        var address = imageBase + ElfHeaderSize + (ulong)(_programHeaderCount * ProgramHeaderSize);

        var segmentStart = imageBase;
        var segmentRank = 0;
        ulong fileBackedEnd = address;
        ulong memoryEnd = address;
        var loads = new List<(int Rank, ulong Start, ulong FileEnd, ulong MemoryEnd)>();

        foreach (var (rank, chunk) in ordered)
        {
            var chunkSegment = rank < RankExecutable ? 0 : rank < RankRelRo ? RankExecutable : RankRelRo;
            if (chunkSegment != segmentRank)
            {
                loads.Add((segmentRank, segmentStart, fileBackedEnd, memoryEnd));
                address = AlignUp(address, pageSize);
                segmentStart = address;
                segmentRank = chunkSegment;
                fileBackedEnd = memoryEnd = address;
                if (chunkSegment == RankRelRo)
                {
                    _relRoStart = address;
                }
            }

            if (rank >= RankData && _relRoEnd == 0 && _relRoStart != 0)
            {
                // The loader can only protect whole pages, so the read-only-after-relocation part ends on a page boundary.
                address = AlignUp(address, pageSize);
                _relRoEnd = address;
            }

            chunk.Address = AlignUp(address, chunk.Alignment);
            chunk.FileOffset = chunk.Address - imageBase;
            _chunks.Add(chunk);

            if (chunk is { IsTls: true, IsNoBits: true })
            {
                // Zero-initialized TLS only exists in the per-thread copies; it takes no space in the image.
                continue;
            }

            address = chunk.Address + chunk.Size;
            memoryEnd = address;
            if (!chunk.IsNoBits)
            {
                fileBackedEnd = address;
            }

            if (chunk.IsExecutable)
            {
                _textEnd = address;
            }
        }

        if (_relRoStart != 0 && _relRoEnd == 0)
        {
            _relRoEnd = AlignUp(address, pageSize);
        }

        // The loader changes the protection of the whole RELRO region, so all of it must be mapped. When nothing
        // follows the region, the padding up to its page-aligned end becomes zero-filled memory of the segment.
        memoryEnd = Math.Max(memoryEnd, _relRoEnd);
        loads.Add((segmentRank, segmentStart, fileBackedEnd, memoryEnd));

        _dataEnd = fileBackedEnd;
        _imageEnd = memoryEnd;

        if (tls.Count > 0)
        {
            var last = tls[^1];
            _context.TlsStart = tls[0].Address;
            _context.TlsSize = last.Address + last.Size - tls[0].Address;
            _context.TlsAlignment = tls.Max(static c => c.Alignment);
        }

        BuildSegments(loads, tls, hasEhFrameHeader);
        AssignSectionIndices(fileBackedEnd - imageBase);
    }

    private void BuildSegments(List<(int Rank, ulong Start, ulong FileEnd, ulong MemoryEnd)> loads, List<OutputChunk> tls, bool hasEhFrameHeader)
    {
        var imageBase = _context.ImageBase;
        var pageSize = _target.PageSize;
        var headersSize = (ulong)(_programHeaderCount * ProgramHeaderSize);

        _segments.Add(new Segment(SegmentType.Phdr, SegmentFlags.Read, ElfHeaderSize, imageBase + ElfHeaderSize, headersSize, headersSize, 8));
        _segments.Add(new Segment(SegmentType.Interp, SegmentFlags.Read, _interp!.FileOffset, _interp.Address, _interp.Size, _interp.Size, 1));

        foreach (var (rank, start, fileEnd, memoryEnd) in loads)
        {
            var flags = rank switch
            {
                RankExecutable => SegmentFlags.Read | SegmentFlags.Execute,
                RankRelRo => SegmentFlags.Read | SegmentFlags.Write,
                _ => SegmentFlags.Read,
            };
            _segments.Add(new Segment(SegmentType.Load, flags, start - imageBase, start, fileEnd - start, memoryEnd - start, pageSize));
        }

        // Always emit exactly three PT_LOAD headers so the header count computed before layout stays valid.
        for (var i = loads.Count; i < 3; i++)
        {
            _segments.Add(new Segment(SegmentType.Load, SegmentFlags.Read, 0, imageBase, 0, 0, pageSize));
        }

        _segments.Add(new Segment(SegmentType.Dynamic, SegmentFlags.Read | SegmentFlags.Write, _dynamic.FileOffset, _dynamic.Address, _dynamic.Size, _dynamic.Size, 8));

        if (tls.Count > 0)
        {
            var initialized = tls.Where(static c => !c.IsNoBits).Sum(static c => (long)c.Size);
            _segments.Add(new Segment(
                SegmentType.Tls, SegmentFlags.Read, tls[0].FileOffset, _context.TlsStart, (ulong)initialized, _context.TlsSize, _context.TlsAlignment));
        }

        if (hasEhFrameHeader)
        {
            _segments.Add(new Segment(
                SegmentType.GnuEhFrame, SegmentFlags.Read, _ehFrameHeader.FileOffset, _ehFrameHeader.Address, _ehFrameHeader.Size, _ehFrameHeader.Size, 4));
        }

        // The stack is never executable.
        _segments.Add(new Segment(SegmentType.GnuStack, SegmentFlags.Read | SegmentFlags.Write, 0, 0, 0, 0, 16));
        _segments.Add(new Segment(
            SegmentType.GnuRelRo, SegmentFlags.Read, _relRoStart - imageBase, _relRoStart, _relRoEnd - _relRoStart, _relRoEnd - _relRoStart, 1));
    }

    /// <summary>Numbers the sections and places the parts of the file that are not loaded into memory.</summary>
    private void AssignSectionIndices(ulong loadedFileEnd)
    {
        var index = 1;
        foreach (var chunk in _chunks)
        {
            chunk.SectionIndex = index++;
        }

        if (_options.EmitSymbolTable)
        {
            var strings = new StringTableChunk(".strtab", 0);
            var symbols = new SymbolTableChunk(strings);
            foreach (var file in _objects)
            {
                foreach (var symbol in file.Symbols)
                {
                    if (symbol is { IsLocal: true, Kind: SymbolKind.Defined, Section.IsLive: true, Name.Length: > 0 }
                        && symbol.Type is SymbolType.Func or SymbolType.Object)
                    {
                        symbols.Add(symbol);
                    }
                }
            }

            foreach (var symbol in _globals.Values)
            {
                if (symbol is { Kind: SymbolKind.Defined, Section.IsLive: true })
                {
                    symbols.Add(symbol);
                }
            }

            symbols.Finish();
            _nonAllocChunks.Add(symbols);
            _nonAllocChunks.Add(strings);
        }

        _sectionNames = new StringTableChunk(".shstrtab", 0);
        _nonAllocChunks.Add(_sectionNames);
        foreach (var chunk in _chunks.Concat(_nonAllocChunks))
        {
            _sectionNames.Add(chunk.Name);
        }

        var offset = loadedFileEnd;
        foreach (var chunk in _nonAllocChunks)
        {
            chunk.SectionIndex = index++;
            offset = AlignUp(offset, chunk.Alignment);
            chunk.FileOffset = offset;
            offset += chunk.Size;
        }

        _fileSize = AlignUp(offset, 8) + (ulong)(index * SectionHeaderSize);
    }

    /// <summary>Gives the linker-defined symbols their final addresses.</summary>
    private void FinalizeLinkerSymbols()
    {
        var imageBase = _context.ImageBase;

        (ulong Start, ulong End) Bounds(string sectionName) =>
            _outputSections.TryGetValue(sectionName, out var section) ? (section.Address, section.Address + section.Size) : (imageBase, imageBase);

        foreach (var symbol in _linkerSymbols)
        {
            if (symbol.StartStopSection is { } delimited)
            {
                var (start, end) = Bounds(delimited);
                symbol.Value = symbol.Name.StartsWith("__start_", StringComparison.Ordinal) ? start : end;
                continue;
            }

            symbol.Value = symbol.Name switch
            {
                "_GLOBAL_OFFSET_TABLE_" => _context.Got.Size > 0 ? _context.Got.Address : imageBase,
                "_DYNAMIC" => _dynamic.Address,
                "__init_array_start" => Bounds(".init_array").Start,
                "__init_array_end" => Bounds(".init_array").End,
                "__fini_array_start" => Bounds(".fini_array").Start,
                "__fini_array_end" => Bounds(".fini_array").End,
                "__preinit_array_start" => Bounds(".preinit_array").Start,
                "__preinit_array_end" => Bounds(".preinit_array").End,
                "_end" or "end" => _imageEnd,
                "_edata" or "edata" => _dataEnd,
                "_etext" or "etext" => _textEnd,
                "__bss_start" => _outputSections.TryGetValue(".bss", out var bss) ? bss.Address : _dataEnd,
                "__GNU_EH_FRAME_HDR" => _ehFrameHeader.Address,
                _ => imageBase, // __ehdr_start, __executable_start, __dso_handle
            };
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Writing
    // ---------------------------------------------------------------------------------------------------------------

    private LinkResult WriteOutput()
    {
        var image = new byte[_fileSize];
        WriteElfHeader(image);
        WriteProgramHeaders(image);

        foreach (var chunk in _chunks.Concat(_nonAllocChunks))
        {
            if (!chunk.IsNoBits)
            {
                chunk.Write(image.AsSpan((int)chunk.FileOffset, (int)chunk.Size), _context);
            }
        }

        WriteSectionHeaders(image);

        var directory = Path.GetDirectoryName(Path.GetFullPath(_options.OutputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(_options.OutputPath, image);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                _options.OutputPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        var liveSections = AllSections().Count(static s => s.IsLive);
        var totalSections = AllSections().Count();
        _logger.LogDebug(
            "Linked {Objects} objects, {Live} of {Total} sections, {Imports} imports, {Relocations} dynamic relocations",
            _objects.Count, liveSections, totalSections, _imports.Count, _context.DynamicRelocations.Count);

        return new LinkResult(
            _options.OutputPath,
            image.LongLength,
            _objects.Count,
            liveSections,
            totalSections - liveSections,
            _neededLibraries,
            _imports.Select(static s => s.Name).ToList(),
            _versions.RequiredVersions.ToList());
    }

    private void WriteElfHeader(Span<byte> image)
    {
        image[0] = 0x7f;
        image[1] = (byte)'E';
        image[2] = (byte)'L';
        image[3] = (byte)'F';
        image[4] = 2; // ELFCLASS64
        image[5] = 1; // ELFDATA2LSB
        image[6] = 1; // EV_CURRENT
        LittleEndian.U16(image, 0x10, _context.IsPositionIndependent ? ElfType.Dyn : ElfType.Exec);
        LittleEndian.U16(image, 0x12, (ushort)_target.Machine);
        LittleEndian.U32(image, 0x14, 1);
        LittleEndian.U64(image, 0x18, _globals[EntrySymbolName].Address);
        LittleEndian.U64(image, 0x20, ElfHeaderSize);
        LittleEndian.U64(image, 0x28, _fileSize - (ulong)((_chunks.Count + _nonAllocChunks.Count + 1) * SectionHeaderSize));
        LittleEndian.U16(image, 0x34, ElfHeaderSize);
        LittleEndian.U16(image, 0x36, ProgramHeaderSize);
        LittleEndian.U16(image, 0x38, (ushort)_segments.Count);
        LittleEndian.U16(image, 0x3a, SectionHeaderSize);
        LittleEndian.U16(image, 0x3c, (ushort)(_chunks.Count + _nonAllocChunks.Count + 1));
        LittleEndian.U16(image, 0x3e, (ushort)_sectionNames.SectionIndex);
    }

    private void WriteProgramHeaders(Span<byte> image)
    {
        if (_segments.Count != _programHeaderCount)
        {
            throw new InvalidOperationException($"program header count changed after layout ({_programHeaderCount} -> {_segments.Count})");
        }

        for (var i = 0; i < _segments.Count; i++)
        {
            var segment = _segments[i];
            var header = image.Slice(ElfHeaderSize + i * ProgramHeaderSize, ProgramHeaderSize);
            LittleEndian.U32(header, 0, segment.Type);
            LittleEndian.U32(header, 4, segment.Flags);
            LittleEndian.U64(header, 8, segment.Offset);
            LittleEndian.U64(header, 16, segment.Address);
            LittleEndian.U64(header, 24, segment.Address);
            LittleEndian.U64(header, 32, segment.FileSize);
            LittleEndian.U64(header, 40, segment.MemorySize);
            LittleEndian.U64(header, 48, segment.Alignment);
        }
    }

    private void WriteSectionHeaders(Span<byte> image)
    {
        var tableOffset = (int)LittleEndian.U64(image, 0x28);
        foreach (var chunk in _chunks.Concat(_nonAllocChunks))
        {
            var header = image.Slice(tableOffset + chunk.SectionIndex * SectionHeaderSize, SectionHeaderSize);
            LittleEndian.U32(header, 0, _sectionNames.Add(chunk.Name));
            LittleEndian.U32(header, 4, chunk.Type);
            LittleEndian.U64(header, 8, chunk.Flags);
            LittleEndian.U64(header, 16, chunk.IsAlloc ? chunk.Address : 0);
            LittleEndian.U64(header, 24, chunk.FileOffset);
            LittleEndian.U64(header, 32, chunk.Size);
            LittleEndian.U32(header, 40, (uint)(chunk.LinkedChunk?.SectionIndex ?? 0));
            LittleEndian.U32(header, 44, chunk.Info);
            LittleEndian.U64(header, 48, chunk.Alignment);
            LittleEndian.U64(header, 56, chunk.EntrySize);
        }
    }
}
