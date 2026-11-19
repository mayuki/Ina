using System.Security.Cryptography;
using System.Text;
using Ina.Linker.Elf;

namespace Ina.Linker.MachO;

internal sealed partial class MachLinkSession
{
    /// <summary>
    /// The C library of macOS. Every image has to depend on it, whether or not it uses one of its symbols: the
    /// loader relies on it to set up the process.
    /// </summary>
    private const string SystemLibrary = "/usr/lib/libSystem.B.dylib";

    private const string DefaultDynamicLoader = "/usr/lib/dyld";
    private const uint PlatformMacOS = 1;

    private static readonly Version FallbackMinimumOSVersion = new(11, 0);

    private readonly List<MachOutputSegment> _segments = [];
    private readonly List<MachOutputSection> _sections = [];
    private readonly Dictionary<(string Segment, string Section), MachOutputSection> _sectionsByName = [];
    private readonly List<MachOutputSection> _codeSections = [];
    private MachOutputSegment _text = null!;
    private List<string> _libraries = [];
    private int _loadCommandsSize;
    private ulong _linkEditAddress;
    private ulong _linkEditFileOffset;

    private string DynamicLoader => _options.Imports.Interpreter.Length > 0 ? _options.Imports.Interpreter : DefaultDynamicLoader;

    private static ulong AlignUp(ulong value, ulong alignment) => (value + alignment - 1) & ~(alignment - 1);

    private static int AlignUp(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);

    // ---------------------------------------------------------------------------------------------------------------
    // Output sections and layout
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Groups the live atoms into output sections, adds the generated sections and orders everything.</summary>
    private void CreateOutputSections()
    {
        // Relocation bookkeeping bits and the debug attribute describe the section in an object file, not in an image.
        const uint inputOnlyFlags = MachSectionType.AttrDebug | 0x300;

        foreach (var atom in LiveAtoms())
        {
            var input = atom.Section;
            var key = MachSectionLayout.OutputName(input.SegmentName, input.SectionName);
            if (!_sectionsByName.TryGetValue(key, out var section))
            {
                section = AddSection(new MachOutputSection(key.Segment, key.Section, input.Flags & ~inputOnlyFlags));
            }
            else if (section.IsZeroFill != input.IsZeroFill)
            {
                throw new LinkerException($"{input.DisplayName}: zero-filled and initialized sections with the same name cannot be merged");
            }

            section.Add(atom);
        }

        _codeSections.AddRange(_sections.Where(static section => section.IsCode));

        if (_context.Stubs.Count > 0)
        {
            _context.StubsSection = AddSection(new MachStubsSection(_target));
        }

        if (_context.GotEntries.Count > 0)
        {
            _context.GotSection = AddSection(new MachGotSection());
        }

        MachImageInfoSection? imageInfo = null;
        foreach (var file in _objects)
        {
            foreach (var section in file.Sections)
            {
                if (section.Kind == MachSectionKind.ObjCImageInfo)
                {
                    (imageInfo ??= (MachImageInfoSection)AddSection(new MachImageInfoSection())).Merge(section);
                }
            }
        }

        if (_unwindRecords.Count > 0)
        {
            var ehFrame = new MachEhFrameSection();
            AddSection(new MachUnwindInfoSection(_unwindRecords, _codeSections, ehFrame));
            if (_unwindRecords.Exists(static record => record.Fde is not null))
            {
                AddSection(ehFrame);
            }
        }

        // Segments in the conventional order; the text segment always exists because it holds the header.
        _text = new MachOutputSegment(MachSectionLayout.TextSegment, SegmentProtection.Read | SegmentProtection.Execute);
        _segments.Add(_text);
        var names = _sections.Select(static section => section.SegmentName).Distinct().ToList();
        foreach (var name in new[] { MachSectionLayout.DataConstSegment, MachSectionLayout.DataSegment }.Concat(names).Distinct())
        {
            if (name == MachSectionLayout.TextSegment || !names.Contains(name))
            {
                continue;
            }

            var flags = name == MachSectionLayout.DataConstSegment ? SegmentProtection.FlagReadOnly : 0;
            _segments.Add(new MachOutputSegment(name, SegmentProtection.Read | SegmentProtection.Write, flags));
        }

        var sectionIndex = 1;
        foreach (var segment in _segments)
        {
            segment.Sections.AddRange(_sections.Where(section => section.SegmentName == segment.Name).OrderBy(MachSectionLayout.Rank));
            foreach (var section in segment.Sections)
            {
                section.Index = sectionIndex++;
            }
        }

        // Dependencies in the order of the import table: the C library, and every library a symbol is taken from.
        var used = new HashSet<string>(_imports.Select(static symbol => symbol.Import!.Library), StringComparer.Ordinal);
        _libraries = _options.Imports.Libraries.Where(library => library == SystemLibrary || used.Contains(library)).ToList();
    }

    private MachOutputSection AddSection(MachOutputSection section)
    {
        _sections.Add(section);
        _sectionsByName[(section.SegmentName, section.SectionName)] = section;
        return section;
    }

    private static int DylibCommandSize(string name) => AlignUp(24 + Encoding.UTF8.GetByteCount(name) + 1, 8);

    private int DylinkerCommandSize => AlignUp(12 + Encoding.UTF8.GetByteCount(DynamicLoader) + 1, 8);

    /// <summary>
    /// The size of the load commands. The first section follows them directly, so it has to be known before the
    /// layout, when the content of most commands is not.
    /// </summary>
    private int ComputeLoadCommandsSize()
    {
        var size = LoadCommand.SegmentSize * (_segments.Count + 2); // __PAGEZERO and __LINKEDIT have no sections
        size += _segments.Sum(static segment => segment.Sections.Count) * LoadCommand.SectionSize;
        size += 48 + 24 + 80;                                      // LC_DYLD_INFO_ONLY, LC_SYMTAB, LC_DYSYMTAB
        size += DylinkerCommandSize;
        size += 24 + 24 + 24;                                      // LC_UUID, LC_BUILD_VERSION, LC_MAIN
        size += _libraries.Sum(DylibCommandSize);
        size += 16 + 16;                                           // LC_FUNCTION_STARTS, LC_CODE_SIGNATURE
        return size;
    }

    private void AssignAddresses()
    {
        var pageSize = _target.PageSize;
        _loadCommandsSize = ComputeLoadCommandsSize();

        var address = _context.ImageBase;
        var fileOffset = 0UL;
        var segmentIndex = 1; // __PAGEZERO is segment 0
        foreach (var segment in _segments)
        {
            segment.Index = segmentIndex++;
            segment.Address = address;
            segment.FileOffset = fileOffset;

            // The header and the load commands are the beginning of the text segment.
            var cursor = segment == _text ? (ulong)(MachHeader.Size + _loadCommandsSize) : 0;
            var fileEnd = cursor;
            var seenZeroFill = false;
            foreach (var section in segment.Sections)
            {
                section.Finish(_context);
                cursor = AlignUp(cursor, section.Alignment);
                section.Address = segment.Address + cursor;
                cursor += section.Size;
                if (section.IsZeroFill)
                {
                    seenZeroFill = true;
                }
                else
                {
                    if (seenZeroFill)
                    {
                        throw new LinkerException($"section {section} cannot follow a zero-filled section in its segment");
                    }

                    section.FileOffset = segment.FileOffset + (section.Address - segment.Address);
                    fileEnd = cursor;
                }
            }

            segment.VmSize = AlignUp(cursor, pageSize);
            segment.FileSize = AlignUp(fileEnd, pageSize);
            address += segment.VmSize;
            fileOffset += segment.FileSize;
        }

        _linkEditAddress = address;
        _linkEditFileOffset = fileOffset;

        var threadLocalData = _sections
            .Where(static section => section.Type is MachSectionType.ThreadLocalRegular or MachSectionType.ThreadLocalZeroFill)
            .Select(static section => section.Address)
            .ToList();
        if (threadLocalData.Count > 0)
        {
            _context.ThreadLocalTemplateStart = threadLocalData.Min();
        }
    }

    /// <summary>Gives the linker-defined symbols their addresses, which are only known after layout.</summary>
    private void FinalizeLinkerSymbols()
    {
        foreach (var symbol in _linkerSymbols)
        {
            // A section or segment that does not exist is empty: its start and end are the same address.
            symbol.Value = _context.ImageBase;
            if (symbol.Boundary is not { } boundary)
            {
                continue;
            }

            if (boundary.Section is { } sectionName)
            {
                if (_sectionsByName.TryGetValue((boundary.Segment, sectionName), out var section))
                {
                    symbol.Value = boundary.IsEnd ? section.Address + section.Size : section.Address;
                }
            }
            else if (_segments.Find(segment => segment.Name == boundary.Segment) is { } segment)
            {
                symbol.Value = boundary.IsEnd ? segment.Address + segment.VmSize : segment.Address;
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Writing
    // ---------------------------------------------------------------------------------------------------------------

    private bool IsWeakImport(MachSymbol symbol) => !symbol.HasStrongReference;

    private MachFixupLocation Locate(ulong address)
    {
        foreach (var segment in _segments)
        {
            if (address >= segment.Address && address < segment.Address + segment.VmSize)
            {
                return new MachFixupLocation(segment.Index, address - segment.Address);
            }
        }

        throw new InvalidOperationException($"fixup at 0x{address:x} is outside of all segments");
    }

    private LinkResult WriteOutput()
    {
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < _libraries.Count; i++)
        {
            ordinals[_libraries[i]] = i + 1;
        }

        // Fixups: what the loader has to patch when it maps the image.
        var rebases = new List<MachFixupLocation>(_context.Rebases.Count + _context.GotEntries.Count);
        var binds = new List<MachBindFixup>(_context.Binds.Count + _context.GotEntries.Count);
        foreach (var rebase in _context.Rebases)
        {
            rebases.Add(Locate(rebase.Atom.Address + rebase.Offset));
        }

        foreach (var bind in _context.Binds)
        {
            binds.Add(new MachBindFixup(
                Locate(bind.Atom.Address + bind.Offset), ordinals[bind.Symbol.Import!.Library], bind.Symbol.Name, IsWeakImport(bind.Symbol), bind.Addend));
        }

        foreach (var symbol in _context.GotEntries)
        {
            var slot = Locate(_context.GotAddress(symbol));
            if (symbol.IsImported)
            {
                binds.Add(new MachBindFixup(slot, ordinals[symbol.Import!.Library], symbol.Name, IsWeakImport(symbol), 0));
            }
            else if (symbol.IsImageRelative)
            {
                rebases.Add(slot);
            }
        }

        var rebaseInfo = MachLinkEdit.EncodeRebases(rebases);
        var bindInfo = MachLinkEdit.EncodeBinds(binds);
        var exportInfo = MachLinkEdit.BuildExportTrie(_exports.Select(symbol => (symbol.Name, symbol.Address - _context.ImageBase)));
        var functionStarts = MachLinkEdit.EncodeFunctionStarts(
            _codeSections.SelectMany(static section => section.Atoms).Where(static atom => atom.Size > 0).Select(static atom => atom.Address).ToList(),
            _text.Address);

        var symbolTable = BuildSymbolTable(ordinals, out var localCount, out var exportCount, out var importCount, out var strings);
        var indirectSymbols = BuildIndirectSymbolTable();

        // __LINKEDIT, in the order the system tools expect; the signature comes last and covers everything before it.
        var offset = _linkEditFileOffset;
        var rebaseOffset = offset;
        offset += (ulong)rebaseInfo.Length;
        var bindOffset = offset;
        offset += (ulong)bindInfo.Length;
        var exportOffset = offset;
        offset += (ulong)exportInfo.Length;
        var functionStartsOffset = offset;
        offset += (ulong)functionStarts.Length;
        var symbolOffset = offset;
        offset += (ulong)symbolTable.Length;
        var indirectOffset = offset;
        offset += (ulong)indirectSymbols.Length;
        var stringOffset = offset;
        var signatureOffset = AlignUp(offset + (ulong)strings.Length, 16);
        var stringSize = signatureOffset - stringOffset;

        var identifier = Path.GetFileName(_options.OutputPath);
        var signatureSize = (ulong)MachCodeSignature.Size((long)signatureOffset, identifier);
        var fileSize = signatureOffset + signatureSize;
        var linkEditFileSize = fileSize - _linkEditFileOffset;

        var image = new byte[fileSize];
        rebaseInfo.CopyTo(image, (int)rebaseOffset);
        bindInfo.CopyTo(image, (int)bindOffset);
        exportInfo.CopyTo(image, (int)exportOffset);
        functionStarts.CopyTo(image, (int)functionStartsOffset);
        symbolTable.CopyTo(image, (int)symbolOffset);
        indirectSymbols.CopyTo(image, (int)indirectOffset);
        strings.CopyTo(image, (int)stringOffset);

        foreach (var section in _sections)
        {
            if (!section.IsZeroFill && section.Size > 0)
            {
                section.Write(_context, image.AsSpan((int)section.FileOffset, (int)section.Size));
            }
        }

        // Header and load commands.
        var commands = new MemoryStream();
        var writer = new BinaryWriter(commands);
        var commandCount = 0;

        void Name16(string name)
        {
            var bytes = new byte[16];
            Encoding.ASCII.GetBytes(name, bytes);
            writer.Write(bytes);
        }

        void Segment(string name, ulong address, ulong vmSize, ulong fileOffset, ulong size, uint protection, uint flags, List<MachOutputSection> sections)
        {
            commandCount++;
            writer.Write(LoadCommand.Segment64);
            writer.Write((uint)(LoadCommand.SegmentSize + sections.Count * LoadCommand.SectionSize));
            Name16(name);
            writer.Write(address);
            writer.Write(vmSize);
            writer.Write(fileOffset);
            writer.Write(size);
            writer.Write(protection);
            writer.Write(protection);
            writer.Write((uint)sections.Count);
            writer.Write(flags);
            foreach (var section in sections)
            {
                Name16(section.SectionName);
                Name16(name);
                writer.Write(section.Address);
                writer.Write(section.Size);
                writer.Write(section.IsZeroFill ? 0u : (uint)section.FileOffset);
                writer.Write((uint)System.Numerics.BitOperations.Log2(section.Alignment));
                writer.Write(0u); // relocation offset
                writer.Write(0u); // relocation count
                writer.Write(section.Flags);
                writer.Write(section.Reserved1);
                writer.Write(section.Reserved2);
                writer.Write(0u);
            }
        }

        void Command(uint command, int size)
        {
            commandCount++;
            writer.Write(command);
            writer.Write((uint)size);
        }

        void PaddedString(string value, int size)
        {
            var bytes = new byte[size];
            Encoding.UTF8.GetBytes(value, bytes);
            writer.Write(bytes);
        }

        Segment("__PAGEZERO", 0, _context.ImageBase, 0, 0, 0, 0, []);
        foreach (var segment in _segments)
        {
            Segment(segment.Name, segment.Address, segment.VmSize, segment.FileOffset, segment.FileSize, segment.Protection, segment.Flags, segment.Sections);
        }

        Segment(
            MachSectionLayout.LinkEditSegment, _linkEditAddress, AlignUp(linkEditFileSize, _target.PageSize), _linkEditFileOffset, linkEditFileSize,
            SegmentProtection.Read, 0, []);

        Command(LoadCommand.DyldInfoOnly, 48);
        writer.Write(rebaseInfo.Length > 0 ? (uint)rebaseOffset : 0);
        writer.Write((uint)rebaseInfo.Length);
        writer.Write(bindInfo.Length > 0 ? (uint)bindOffset : 0);
        writer.Write((uint)bindInfo.Length);
        writer.Write(0u); // weak binds: the image neither overrides nor uses coalesced symbols of other images
        writer.Write(0u);
        writer.Write(0u); // lazy binds: every import is bound when the image is loaded
        writer.Write(0u);
        writer.Write(exportInfo.Length > 0 ? (uint)exportOffset : 0);
        writer.Write((uint)exportInfo.Length);

        Command(LoadCommand.SymTab, 24);
        writer.Write((uint)symbolOffset);
        writer.Write((uint)(symbolTable.Length / MachSymbolType.EntrySize));
        writer.Write((uint)stringOffset);
        writer.Write((uint)stringSize);

        Command(LoadCommand.DySymTab, 80);
        writer.Write(0u);
        writer.Write((uint)localCount);
        writer.Write((uint)localCount);
        writer.Write((uint)exportCount);
        writer.Write((uint)(localCount + exportCount));
        writer.Write((uint)importCount);
        writer.Write(new byte[24]); // table of contents, module table, external references: not used
        writer.Write(indirectSymbols.Length > 0 ? (uint)indirectOffset : 0);
        writer.Write((uint)(indirectSymbols.Length / 4));
        writer.Write(new byte[16]); // relocations: an executable has none

        Command(LoadCommand.LoadDylinker, DylinkerCommandSize);
        writer.Write(12u);
        PaddedString(DynamicLoader, DylinkerCommandSize - 12);

        Command(LoadCommand.Uuid, 24);
        var uuidOffset = MachHeader.Size + (int)commands.Length;
        writer.Write(new byte[16]);

        var minimumOS = MinimumOSVersion();
        Command(LoadCommand.BuildVersion, 24);
        writer.Write(PlatformMacOS);
        writer.Write(EncodeVersion(minimumOS));
        writer.Write(EncodeVersion(_options.Imports.SdkVersion ?? minimumOS));
        writer.Write(0u); // no tool entries

        var entry = _globals[_entrySymbolName];
        Command(LoadCommand.Main, 24);
        writer.Write(entry.Address - _text.Address);
        writer.Write(0UL); // default stack size

        foreach (var library in _libraries)
        {
            // A library from which only optional symbols are used may be missing altogether.
            var symbols = _imports.Where(symbol => symbol.Import!.Library == library).ToList();
            var isWeak = library != SystemLibrary && symbols.Count > 0 && symbols.TrueForAll(IsWeakImport);
            var (compatibility, current) = _options.Imports.LibraryVersion(library);
            var size = DylibCommandSize(library);
            Command(isWeak ? LoadCommand.LoadWeakDylib : LoadCommand.LoadDylib, size);
            writer.Write(24u);
            writer.Write(2u); // timestamp, as all current linkers write it
            writer.Write(EncodeVersion(current));
            writer.Write(EncodeVersion(compatibility));
            PaddedString(library, size - 24);
        }

        Command(LoadCommand.FunctionStarts, 16);
        writer.Write(functionStarts.Length > 0 ? (uint)functionStartsOffset : 0);
        writer.Write((uint)functionStarts.Length);

        Command(LoadCommand.CodeSignature, 16);
        writer.Write((uint)signatureOffset);
        writer.Write((uint)signatureSize);

        writer.Flush();
        if (commands.Length != _loadCommandsSize)
        {
            throw new InvalidOperationException($"size of the load commands changed after layout ({_loadCommandsSize} -> {commands.Length})");
        }

        var flags = MachHeader.NoUndefs | MachHeader.DyldLink | MachHeader.TwoLevel | MachHeader.Pie;
        if (_sections.Exists(static section => section.Type == MachSectionType.ThreadLocalVariables))
        {
            flags |= MachHeader.HasTlvDescriptors;
        }

        LittleEndian.U32(image, 0, MachHeader.Magic64);
        LittleEndian.U32(image, 4, _target.CpuType);
        LittleEndian.U32(image, 8, _target.ExecutableCpuSubtype);
        LittleEndian.U32(image, 12, MachHeader.FileTypeExecute);
        LittleEndian.U32(image, 16, (uint)commandCount);
        LittleEndian.U32(image, 20, (uint)commands.Length);
        LittleEndian.U32(image, 24, flags);
        commands.ToArray().CopyTo(image, MachHeader.Size);

        // The UUID identifies this build to debuggers and crash reports. It is derived from the content, so that
        // identical inputs give identical outputs.
        var signedPart = image.AsSpan(0, (int)signatureOffset);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(signedPart, hash);
        hash[6] = (byte)((hash[6] & 0x0f) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        hash[..16].CopyTo(image.AsSpan(uuidOffset));

        MachCodeSignature.Sign(signedPart, image.AsSpan((int)signatureOffset), identifier, _text.FileOffset, _text.FileSize);
        WriteFile(image);

        return new LinkResult(
            _options.OutputPath,
            image.Length,
            _objects.Count,
            _liveAtomCount,
            _atomCount - _liveAtomCount,
            _libraries,
            _imports.Select(static symbol => symbol.Name).Order(StringComparer.Ordinal).ToList(),
            [$"macOS {minimumOS}"]);
    }

    /// <summary>
    /// The oldest macOS version the executable runs on: what the import table was made for, or what the object
    /// files were compiled for if that is newer.
    /// </summary>
    private Version MinimumOSVersion()
    {
        var version = _options.Imports.MinimumOSVersion;
        foreach (var file in _objects)
        {
            if (file.MinimumOSVersion == 0)
            {
                continue;
            }

            var required = new Version((int)(file.MinimumOSVersion >> 16), (int)((file.MinimumOSVersion >> 8) & 0xff), (int)(file.MinimumOSVersion & 0xff));
            if (version is null || required > version)
            {
                version = required;
            }
        }

        version ??= FallbackMinimumOSVersion;
        return version.Build > 0 ? version : new Version(version.Major, version.Minor);
    }

    /// <summary>Versions in load commands are packed as <c>xxxx.yy.zz</c>.</summary>
    private static uint EncodeVersion(Version version) =>
        ((uint)version.Major << 16) | ((uint)Math.Clamp(version.Minor, 0, 255) << 8) | (uint)Math.Clamp(Math.Max(version.Build, 0), 0, 255);

    /// <summary>
    /// Builds the symbol table: optionally the defined symbols as locals (for debuggers and profilers), then the
    /// exported symbols and the imported ones, which is the order the loader's bookkeeping requires.
    /// </summary>
    private byte[] BuildSymbolTable(Dictionary<string, int> ordinals, out int localCount, out int exportCount, out int importCount, out byte[] strings)
    {
        var table = new MemoryStream();
        var names = new MemoryStream();
        names.WriteByte(0);
        var count = 0;

        void Add(string name, byte type, int section, ushort desc, ulong value)
        {
            Span<byte> entry = stackalloc byte[MachSymbolType.EntrySize];
            LittleEndian.U32(entry, 0, (uint)names.Length);
            entry[4] = type;
            entry[5] = (byte)section;
            LittleEndian.U16(entry, 6, desc);
            LittleEndian.U64(entry, 8, value);
            table.Write(entry);
            names.Write(Encoding.UTF8.GetBytes(name));
            names.WriteByte(0);
            count++;
        }

        if (_options.EmitSymbolTable)
        {
            var seen = new HashSet<MachSymbol>();
            foreach (var file in _objects)
            {
                for (var i = 0; i < file.Nlist.Count; i++)
                {
                    var entry = file.Nlist[i];
                    var symbol = file.Symbols[i];

                    // Names starting with "L" or "l" are labels the assembler or the linker may drop.
                    if (entry.IsStab || !entry.IsDefinedInSection || entry.Name.Length == 0 || entry.Name[0] is 'L' or 'l')
                    {
                        continue;
                    }

                    if (symbol.Kind != MachSymbolKind.Defined || symbol.File != file || symbol.IsExported || symbol.Atom is not { IsLive: true } atom
                        || !seen.Add(symbol))
                    {
                        continue;
                    }

                    Add(symbol.Name, MachSymbolType.Section, atom.Parent!.Index, 0, symbol.Address);
                }
            }

            // Symbols whose storage or code the linker created: common symbols and generated functions.
            foreach (var symbol in _globals.Values)
            {
                if (symbol.File == _internalObject && symbol.Kind == MachSymbolKind.Defined && !symbol.IsExported && symbol.Atom is { IsLive: true } atom)
                {
                    Add(symbol.Name, MachSymbolType.Section, atom.Parent!.Index, 0, symbol.Address);
                }
            }
        }

        localCount = count;
        foreach (var symbol in _exports.OrderBy(static symbol => symbol.Name, StringComparer.Ordinal))
        {
            Add(symbol.Name, MachSymbolType.Section | MachSymbolType.External, symbol.Atom!.Parent!.Index, 0, symbol.Address);
        }

        exportCount = count - localCount;
        foreach (var symbol in _imports.OrderBy(static symbol => symbol.Name, StringComparer.Ordinal))
        {
            symbol.OutputIndex = count;
            var desc = (ushort)(MachSymbolType.LibraryOrdinal(ordinals[symbol.Import!.Library]) | (IsWeakImport(symbol) ? MachSymbolType.WeakReference : 0));
            Add(symbol.Name, MachSymbolType.Undefined | MachSymbolType.External, 0, desc, 0);
        }

        importCount = count - localCount - exportCount;
        strings = names.ToArray();
        return table.ToArray();
    }

    /// <summary>
    /// Builds the indirect symbol table, which tells tools which symbol every stub and GOT entry stands for. The
    /// loader itself works from the bind information.
    /// </summary>
    private byte[] BuildIndirectSymbolTable()
    {
        var symbols = _context.Stubs.Concat(_context.GotEntries).ToList();
        var table = new byte[symbols.Count * 4];
        for (var i = 0; i < symbols.Count; i++)
        {
            LittleEndian.U32(table, i * 4, symbols[i].IsImported ? (uint)symbols[i].OutputIndex : DyldInfo.IndirectSymbolLocal);
        }

        if (_context.StubsSection is { } stubs)
        {
            stubs.Reserved1 = 0;
        }

        if (_context.GotSection is { } got)
        {
            got.Reserved1 = (uint)_context.Stubs.Count;
        }

        return table;
    }

    private void WriteFile(byte[] image)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_options.OutputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // The kernel caches the signature of a file it has executed. Replacing the file instead of overwriting it
        // makes sure that a previous build at the same path does not invalidate the new one.
        if (File.Exists(_options.OutputPath))
        {
            File.Delete(_options.OutputPath);
        }

        File.WriteAllBytes(_options.OutputPath, image);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                _options.OutputPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }
}
