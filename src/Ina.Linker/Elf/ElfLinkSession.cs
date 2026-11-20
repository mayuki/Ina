using Microsoft.Extensions.Logging;

namespace Ina.Linker.Elf;

/// <summary>One invocation of the ELF linker. The passes run in the order of <see cref="Run"/>.</summary>
internal sealed partial class ElfLinkSession
{
    private const string EntrySymbolName = "_start";

    private static readonly string[] LinkerDefinedSymbols =
    [
        "__ehdr_start", "__executable_start", "__dso_handle", "_GLOBAL_OFFSET_TABLE_", "_DYNAMIC",
        "__init_array_start", "__init_array_end", "__fini_array_start", "__fini_array_end",
        "__preinit_array_start", "__preinit_array_end", "_end", "end", "_edata", "edata", "_etext", "etext",
        "__bss_start", "__GNU_EH_FRAME_HDR",
    ];

    private readonly LinkOptions _options;
    private readonly ILogger _logger;
    private readonly ElfTarget _target;
    private readonly ElfLinkContext _context;

    private readonly List<ElfObjectFile> _objects = [];
    private readonly List<ArchiveFile> _archives = [];
    private readonly Dictionary<string, Symbol> _globals = new(StringComparer.Ordinal);
    private readonly HashSet<string> _comdatSignatures = new(StringComparer.Ordinal);
    private readonly Queue<Symbol> _unresolved = new();
    private readonly List<string> _errors = [];
    private readonly HashSet<Symbol> _reportedUndefined = [];

    /// <summary>Hosts sections the linker creates itself, such as the storage of common symbols.</summary>
    private readonly ElfObjectFile _internalObject;
    private readonly List<InputSection> _internalSections = [];

    private readonly List<Symbol> _linkerSymbols = [];
    private readonly List<Symbol> _imports = [];
    private readonly List<Symbol> _exports = [];
    private readonly Dictionary<string, List<InputSection>> _sectionsByName = new(StringComparer.Ordinal);

    public ElfLinkSession(LinkOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
        _target = ElfTarget.Create(options.Architecture);
        _context = new ElfLinkContext(options, _target);
        _internalObject = ElfObjectFile.CreateSynthetic("<ina internal>", _target.Machine);
    }

    public LinkResult Run()
    {
        AddStartupObject();
        LoadInputs();
        RequestExportedSymbols();
        ResolveFromArchives();
        AddCLibraryStubs();
        DefineLinkerSymbols();
        AllocateCommonSymbols();
        MarkLiveFragments();
        ScanRelocations();
        ThrowIfErrors();

        CreateOutputSections();
        FinishDynamicSections();
        AssignAddresses();
        FinalizeLinkerSymbols();
        return WriteOutput();
    }

    private IEnumerable<InputSection> AllSections()
    {
        foreach (var file in _objects)
        {
            foreach (var section in file.Sections)
            {
                if (section is not null)
                {
                    yield return section;
                }
            }
        }

        foreach (var section in _internalSections)
        {
            yield return section;
        }
    }

    private void ThrowIfErrors()
    {
        if (_errors.Count == 0)
        {
            return;
        }

        const int limit = 50;
        var message = string.Join(Environment.NewLine, _errors.Take(limit));
        if (_errors.Count > limit)
        {
            message += $"{Environment.NewLine}... and {_errors.Count - limit} more errors";
        }

        throw new LinkerException(message);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Input loading and symbol resolution
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Adds the generated entry point, which takes the place of the C runtime startup objects.</summary>
    private void AddStartupObject() =>
        AddSyntheticObject("<ina startup>", [_target.CreateEntryPoint(EntrySymbolName, _options.MainSymbol)]);

    /// <summary>
    /// Supplies the C library functions that are not exported by the shared library, for the names that are still
    /// undefined. Inputs that define these names themselves win because they were resolved earlier.
    /// </summary>
    private void AddCLibraryStubs()
    {
        var needed = _target.CreateCLibraryStubs()
            .Where(stub => _globals.TryGetValue(stub.Name, out var symbol) && symbol.Kind == SymbolKind.Undefined)
            .ToList();
        if (needed.Count > 0)
        {
            AddSyntheticObject("<ina libc stubs>", needed);
        }
    }

    /// <summary>Turns linker-generated functions into an object file that takes part in the link like any other.</summary>
    private void AddSyntheticObject(string name, IReadOnlyList<SyntheticFunction> functions)
    {
        var file = ElfObjectFile.CreateSynthetic(name, _target.Machine);
        var sections = new InputSection?[functions.Count + 1];
        var symbols = new List<ElfSymbol> { new(string.Empty, 0, 0, SectionIndex.Undefined, -1, 0, 0) };

        // Every function gets its own section so that unused ones can be discarded.
        for (var i = 0; i < functions.Count; i++)
        {
            var function = functions[i];
            sections[i + 1] = new InputSection(
                file, i + 1, ".text", SectionType.ProgBits, SectionFlags.Alloc | SectionFlags.ExecInstr, (ulong)function.Code.Length, 16, function.Code);
            symbols.Add(new ElfSymbol(
                function.Name, (SymbolBinding.Global << 4) | SymbolType.Func, 0, (ushort)(i + 1), i + 1, 0, (ulong)function.Code.Length));
        }

        for (var i = 0; i < functions.Count; i++)
        {
            var relocations = new Relocation[functions[i].Relocations.Length];
            for (var r = 0; r < relocations.Length; r++)
            {
                var relocation = functions[i].Relocations[r];
                var index = symbols.FindIndex(s => s.Name == relocation.Symbol);
                if (index < 0)
                {
                    index = symbols.Count;
                    symbols.Add(new ElfSymbol(relocation.Symbol, SymbolBinding.Global << 4, 0, SectionIndex.Undefined, -1, 0, 0));
                }

                relocations[r] = new Relocation((ulong)relocation.Offset, relocation.Type, (uint)index, relocation.Addend);
            }

            sections[i + 1]!.Relocations = relocations;
        }

        file.SetSyntheticContent(sections, [.. symbols], firstGlobalSymbol: 1);
        AddObject(file);
    }

    private void LoadInputs()
    {
        foreach (var input in _options.Inputs)
        {
            var data = input.Data.Span;
            if (ArchiveFile.IsArchive(data))
            {
                _archives.Add(ArchiveFile.Parse(Path.GetFileName(input.Name), input.Data, DefinedSymbols));
            }
            else if (ElfObjectFile.IsElf(data))
            {
                AddObject(ElfObjectFile.Parse(Path.GetFileName(input.Name), input.Data, _comdatSignatures.Add));
            }
            else
            {
                throw new LinkerException($"{input.Name}: unknown file format (expected an ELF object or an ar archive)");
            }
        }
    }

    /// <summary>Lists what a member of an archive without a symbol index defines.</summary>
    private static IEnumerable<string>? DefinedSymbols(string memberName, ReadOnlyMemory<byte> data)
    {
        if (!ElfObjectFile.IsElf(data.Span))
        {
            return null;
        }

        var file = ElfObjectFile.Parse(memberName, data, static _ => true);
        return file.ElfSymbols.Skip(file.FirstGlobalSymbol).Where(static symbol => !symbol.IsUndefined).Select(static symbol => symbol.Name);
    }

    /// <summary>
    /// Makes the exported symbols candidates for archive resolution, like <c>-u symbol</c> of other linkers: what is
    /// exported may live in an archive member that nothing else refers to. A symbol that no input defines is simply
    /// not exported.
    /// </summary>
    private void RequestExportedSymbols()
    {
        foreach (var name in _options.ExportedSymbols)
        {
            if (!_globals.TryGetValue(name, out var symbol))
            {
                symbol = new Symbol(name);
                _globals.Add(name, symbol);
            }

            if (symbol.Kind == SymbolKind.Undefined)
            {
                _unresolved.Enqueue(symbol);
            }
        }
    }

    /// <summary>Pulls in archive members until no undefined symbol can be satisfied by an archive any more.</summary>
    private void ResolveFromArchives()
    {
        while (_unresolved.TryDequeue(out var symbol))
        {
            if (symbol.Kind != SymbolKind.Undefined)
            {
                continue;
            }

            foreach (var archive in _archives)
            {
                if (archive.TryTakeMember(symbol.Name, out var memberName, out var memberData))
                {
                    _logger.LogTrace("Loading {Archive}({Member}) for {Symbol}", archive.Name, memberName, symbol.Name);
                    AddObject(ElfObjectFile.Parse($"{archive.Name}({memberName})", memberData, _comdatSignatures.Add));
                    break;
                }
            }
        }
    }

    private void AddObject(ElfObjectFile file)
    {
        if (file.Machine != _target.Machine)
        {
            throw new LinkerException($"{file.Name}: object file was built for a different architecture ({file.Machine})");
        }

        _objects.Add(file);
        var symbols = new Symbol[file.ElfSymbols.Length];
        for (var i = 0; i < symbols.Length; i++)
        {
            var elf = file.ElfSymbols[i];
            symbols[i] = i < file.FirstGlobalSymbol || elf.IsLocal ? CreateLocalSymbol(file, elf) : ResolveGlobalSymbol(file, elf);
        }

        file.Symbols = symbols;
    }

    private static Symbol CreateLocalSymbol(ElfObjectFile file, in ElfSymbol elf)
    {
        var symbol = new Symbol(elf.Name)
        {
            IsLocal = true, File = file, Type = elf.Type, Binding = SymbolBinding.Local, Value = elf.Value, Size = elf.Size,
        };

        if (elf.Section >= 0 && elf.Section < file.Sections.Length && file.Sections[elf.Section] is { } section)
        {
            symbol.Kind = SymbolKind.Defined;
            symbol.Section = section;
        }
        else if (elf.IsAbsolute || elf.IsUndefined)
        {
            symbol.Kind = SymbolKind.Absolute;
        }
        else
        {
            // Defined in a section that is not part of the link (discarded COMDAT copy, debug info): resolves to 0.
            symbol.Kind = SymbolKind.Undefined;
            symbol.Value = 0;
        }

        return symbol;
    }

    private Symbol ResolveGlobalSymbol(ElfObjectFile file, in ElfSymbol elf)
    {
        if (!_globals.TryGetValue(elf.Name, out var symbol))
        {
            symbol = new Symbol(elf.Name);
            _globals.Add(elf.Name, symbol);
        }

        var section = elf.Section >= 0 && elf.Section < file.Sections.Length ? file.Sections[elf.Section] : null;
        var isDefinition = elf.IsAbsolute || elf.IsCommon || section is not null;
        if (!isDefinition)
        {
            // Either a plain reference or a definition in a discarded COMDAT copy, which the kept copy must provide.
            if (elf.Binding != SymbolBinding.Weak || !elf.IsUndefined)
            {
                symbol.HasStrongReference = true;
                if (symbol.Kind == SymbolKind.Undefined)
                {
                    _unresolved.Enqueue(symbol);
                }
            }

            return symbol;
        }

        var isWeak = elf.Binding == SymbolBinding.Weak;
        switch (symbol.Kind)
        {
            case SymbolKind.Undefined:
                Define(symbol, file, elf, section);
                break;

            case SymbolKind.Common:
                if (elf.IsCommon)
                {
                    symbol.Size = Math.Max(symbol.Size, elf.Size);
                    symbol.CommonAlignment = Math.Max(symbol.CommonAlignment, elf.Value);
                }
                else if (!isWeak)
                {
                    Define(symbol, file, elf, section);
                }

                break;

            default:
                if (elf.IsCommon)
                {
                    break;
                }

                if (symbol.IsWeak && !isWeak)
                {
                    Define(symbol, file, elf, section);
                }
                else if (!symbol.IsWeak && !isWeak)
                {
                    _errors.Add($"duplicate symbol: {elf.Name}{Environment.NewLine}>>> defined in {symbol.File?.Name}{Environment.NewLine}>>> defined in {file.Name}");
                }

                break;
        }

        return symbol;
    }

    private static void Define(Symbol symbol, ElfObjectFile file, in ElfSymbol elf, InputSection? section)
    {
        symbol.File = file;
        symbol.Type = elf.Type;
        symbol.Binding = elf.Binding == SymbolBinding.Weak ? SymbolBinding.Weak : SymbolBinding.Global;
        symbol.Visibility = elf.Visibility;
        symbol.Size = elf.Size;
        symbol.Section = null;
        if (elf.IsAbsolute)
        {
            symbol.Kind = SymbolKind.Absolute;
            symbol.Value = elf.Value;
        }
        else if (elf.IsCommon)
        {
            symbol.Kind = SymbolKind.Common;
            symbol.CommonAlignment = elf.Value;
            symbol.Value = 0;
        }
        else
        {
            symbol.Kind = SymbolKind.Defined;
            symbol.Section = section;
            symbol.Value = elf.Value;
        }
    }

    /// <summary>Claims the symbols that the linker itself provides, so that they are no longer undefined.</summary>
    private void DefineLinkerSymbols()
    {
        foreach (var section in AllSections())
        {
            if (SectionNaming.IsCIdentifier(section.Name))
            {
                if (!_sectionsByName.TryGetValue(section.Name, out var list))
                {
                    _sectionsByName[section.Name] = list = [];
                }

                list.Add(section);
            }
        }

        foreach (var name in LinkerDefinedSymbols)
        {
            if (_globals.TryGetValue(name, out var symbol) && symbol.Kind == SymbolKind.Undefined)
            {
                symbol.Kind = SymbolKind.Synthetic;
                symbol.Visibility = SymbolVisibility.Hidden;
                _linkerSymbols.Add(symbol);
            }
        }

        foreach (var symbol in _globals.Values)
        {
            if (symbol.Kind != SymbolKind.Undefined)
            {
                continue;
            }

            // __start_X / __stop_X delimit the output section X when X is a valid C identifier.
            var sectionName = symbol.Name.StartsWith("__start_", StringComparison.Ordinal) ? symbol.Name["__start_".Length..]
                : symbol.Name.StartsWith("__stop_", StringComparison.Ordinal) ? symbol.Name["__stop_".Length..]
                : null;
            if (sectionName is not null && _sectionsByName.ContainsKey(sectionName))
            {
                symbol.Kind = SymbolKind.Synthetic;
                symbol.Visibility = SymbolVisibility.Hidden;
                symbol.StartStopSection = sectionName;
                _linkerSymbols.Add(symbol);
            }
        }

        foreach (var name in _options.ExportedSymbols)
        {
            if (_globals.TryGetValue(name, out var symbol) && symbol.Kind == SymbolKind.Defined)
            {
                symbol.IsExported = true;
                _exports.Add(symbol);
            }
            else
            {
                _logger.LogDebug("Exported symbol {Symbol} is not defined by any input", name);
            }
        }
    }

    /// <summary>Gives every common symbol that never got a real definition its own zero-initialized storage.</summary>
    private void AllocateCommonSymbols()
    {
        foreach (var symbol in _globals.Values)
        {
            if (symbol.Kind != SymbolKind.Common)
            {
                continue;
            }

            var section = new InputSection(
                _internalObject, -1, ".bss", SectionType.NoBits, SectionFlags.Alloc | SectionFlags.Write, symbol.Size,
                Math.Max(symbol.CommonAlignment, 1), ReadOnlyMemory<byte>.Empty);
            _internalSections.Add(section);
            symbol.Kind = SymbolKind.Defined;
            symbol.Section = section;
            symbol.Value = 0;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Garbage collection and relocation scanning
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Marks everything reachable from the entry point. Unwind records do not keep code alive; instead an FDE
    /// becomes live together with the code it describes, and then keeps its own references (LSDA, personality) alive.
    /// </summary>
    private void MarkLiveFragments()
    {
        var unwindRecords = new Dictionary<InputSection, List<EhFrameRecord>>();
        foreach (var file in _objects)
        {
            foreach (var record in file.EhFrameRecords)
            {
                if (record is { IsCie: false, Target: { } target })
                {
                    if (!unwindRecords.TryGetValue(target, out var list))
                    {
                        unwindRecords[target] = list = [];
                    }

                    list.Add(record);
                }
            }
        }

        var pending = new Stack<Fragment>();

        void Mark(InputSection section)
        {
            if (section.IsLive)
            {
                return;
            }

            section.IsLive = true;
            pending.Push(section);
            if (!unwindRecords.TryGetValue(section, out var records))
            {
                return;
            }

            foreach (var record in records)
            {
                record.IsLive = true;
                pending.Push(record);
                if (record.Cie is { IsLive: false } cie)
                {
                    cie.IsLive = true;
                    pending.Push(cie);
                }
            }
        }

        if (!_options.GarbageCollectSections)
        {
            foreach (var section in AllSections())
            {
                Mark(section);
            }

            return;
        }

        if (_globals.TryGetValue(EntrySymbolName, out var entry) && entry.Section is { } entrySection)
        {
            Mark(entrySection);
        }

        foreach (var symbol in _exports)
        {
            Mark(symbol.Section!);
        }

        foreach (var section in AllSections())
        {
            if (IsGarbageCollectionRoot(section))
            {
                Mark(section);
            }
        }

        while (pending.TryPop(out var fragment))
        {
            var symbols = fragment.File.Symbols;
            foreach (ref readonly var relocation in fragment.Relocations.AsSpan())
            {
                var symbol = symbols[relocation.SymbolIndex];
                if (symbol.Section is { } target)
                {
                    Mark(target);
                }
                else if (symbol.StartStopSection is { } name)
                {
                    foreach (var section in _sectionsByName[name])
                    {
                        Mark(section);
                    }
                }
            }
        }
    }

    private static bool IsGarbageCollectionRoot(InputSection section)
    {
        if ((section.Flags & SectionFlags.GnuRetain) != 0)
        {
            return true;
        }

        // Constructors and destructors run without being referenced by anything.
        return section.Type is SectionType.InitArray or SectionType.FiniArray or SectionType.PreInitArray
            || SectionNaming.OutputName(section.Name) is ".ctors" or ".dtors" or ".init" or ".fini";
    }

    private IEnumerable<Fragment> LiveFragments()
    {
        foreach (var section in AllSections())
        {
            if (section.IsLive)
            {
                yield return section;
            }
        }

        foreach (var file in _objects)
        {
            foreach (var record in file.EhFrameRecords)
            {
                if (record.IsLive)
                {
                    yield return record;
                }
            }
        }
    }

    /// <summary>
    /// Walks the relocations of all live fragments to find out which symbols are imported and which GOT entries,
    /// PLT stubs and dynamic relocations are needed. Their number determines the size of the generated sections.
    /// </summary>
    private void ScanRelocations()
    {
        _target.Prepare(_context, LiveFragments());
        foreach (var fragment in LiveFragments())
        {
            var symbols = fragment.File.Symbols;
            var relocations = fragment.Relocations;
            for (var i = 0; i < relocations.Length;)
            {
                var symbol = symbols[relocations[i].SymbolIndex];
                if (symbol.Kind == SymbolKind.Undefined)
                {
                    ClassifyUndefined(symbol, fragment);
                }

                try
                {
                    // A target may handle a sequence of relocations at once; the ones it consumed are not scanned,
                    // so a symbol that is only referenced by rewritten code is never imported.
                    i += _target.ScanAt(_context, fragment, i, symbol);
                }
                catch (LinkerException e)
                {
                    _errors.Add(e.Message);
                    i++;
                }
            }
        }
    }

    /// <summary>Decides what a symbol without a definition is: an import, an unresolved weak symbol, or an error.</summary>
    private void ClassifyUndefined(Symbol symbol, Fragment referencedBy)
    {
        if (symbol.IsLocal)
        {
            return;
        }

        if (_options.Imports.TryGet(symbol.Name, out var import))
        {
            symbol.Kind = SymbolKind.Imported;
            symbol.Import = import;
            _imports.Add(symbol);
            return;
        }

        if (symbol.HasStrongReference && _reportedUndefined.Add(symbol))
        {
            _errors.Add($"undefined symbol: {symbol.Name}{Environment.NewLine}>>> referenced by {referencedBy.DisplayName}");
        }
    }
}
