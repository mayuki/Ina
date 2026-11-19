using Ina.Linker.Input;
using Microsoft.Extensions.Logging;

namespace Ina.Linker.Pe;

/// <summary>One invocation of the PE linker. The passes run in the order of <see cref="Run"/>.</summary>
internal sealed partial class PeLinkSession
{
    private const string ImportPrefix = "__imp_";

    /// <summary>The start of the image; compilers use it to turn addresses into image-relative ones at run time.</summary>
    private const string ImageBaseSymbol = "__ImageBase";

    private const string TlsDirectorySymbol = "_tls_used";
    private const string LoadConfigurationSymbol = "_load_config_used";

    private readonly PeLinkOptions _options;
    private readonly ILogger _logger;
    private readonly PeTarget _target;
    private readonly PeLinkContext _context;

    private readonly List<CoffObject> _objects = [];
    private readonly List<ArchiveFile> _archives = [];
    private readonly Dictionary<string, PeSymbol> _globals = new(StringComparer.Ordinal);
    private readonly Queue<PeSymbol> _unresolved = new();
    private readonly List<string> _errors = [];
    private readonly HashSet<PeSymbol> _reportedUndefined = [];

    /// <summary>Generated objects by the symbols they define; an object is removed once it has been added to the link.</summary>
    private readonly Dictionary<string, PeRuntimeObject> _runtimeSupport = new(StringComparer.Ordinal);

    /// <summary>What <c>/alternatename</c> options of the inputs map an undefined symbol to.</summary>
    private readonly Dictionary<string, string> _alternateNames = new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> _merges = new(StringComparer.Ordinal);

    /// <summary>Symbols that must be kept although nothing refers to them: the entry point, exports, <c>/include</c>.</summary>
    private readonly List<PeSymbol> _roots = [];

    private readonly List<string> _exportNames = [];
    private readonly Dictionary<(string Library, string Name), PeImport> _imports = [];
    private readonly List<PeChunk> _generatedChunks = [];

    /// <summary>A chunk without content at the very start of the image, for symbols that denote the image base.</summary>
    private readonly PeGeneratedChunk _imageStart = new(string.Empty, 0, 1, 0) { IsLive = true };

    public PeLinkSession(PeLinkOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
        _target = PeTarget.Create(options.Architecture);
        _context = new PeLinkContext(options, _target);

        // Sections that every Windows linker folds into the three standard ones.
        foreach (var (from, to) in new[] { (".xdata", ".rdata"), (".CRT", ".rdata"), (".idata", ".rdata"), (".edata", ".rdata"), (".00cfg", ".rdata"), (".bss", ".data") })
        {
            _merges[from] = to;
        }

        foreach (var (from, to) in options.MergedSections)
        {
            _merges[from] = to;
        }

        foreach (var runtimeObject in _target.CreateRuntimeSupport(options))
        {
            foreach (var symbol in runtimeObject.Defines)
            {
                _runtimeSupport[symbol] = runtimeObject;
            }
        }
    }

    public LinkResult Run()
    {
        LoadInputs();
        AddRoots();
        ResolveSymbols();
        AllocateCommonSymbols();
        BindImports();
        MarkLiveChunks();
        ScanRelocations();
        ThrowIfErrors();

        CreateImportTables();
        CreateExportTable();
        ThrowIfErrors();

        AssignAddresses();
        CreateBaseRelocations();
        return WriteOutput();
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

    private void LoadInputs()
    {
        foreach (var input in _options.Inputs)
        {
            var data = input.Data.Span;
            var name = Path.GetFileName(input.Name);
            if (ArchiveFile.IsArchive(data))
            {
                _archives.Add(ArchiveFile.Parse(name, input.Data, DefinedSymbols));
            }
            else if (CoffObject.IsObject(data))
            {
                AddObject(CoffObject.Parse(name, input.Data));
            }
            else
            {
                throw new LinkerException($"{input.Name}: unknown file format (expected a COFF object or a static library)");
            }
        }
    }

    /// <summary>Lists what a member of a library without a symbol index defines.</summary>
    private static IEnumerable<string> DefinedSymbols(string name, ReadOnlyMemory<byte> data)
    {
        if (!CoffObject.IsObject(data.Span))
        {
            return [];
        }

        var file = CoffObject.Parse(name, data);
        return file.Records.Where(static r => r is { IsAuxiliary: false, IsExternal: true } && (r.SectionNumber != 0 || r.IsCommon)).Select(static r => r.Name);
    }

    /// <summary>References the symbols that the image needs regardless of what the inputs refer to.</summary>
    private void AddRoots()
    {
        Require(_options.EntryPoint);
        foreach (var name in _options.ExportedSymbols)
        {
            AddExport(name);
        }

        // Compilers refer to the image base by this name; it is a symbol of the linker, not of any object.
        var imageBase = GetOrAddGlobal(ImageBaseSymbol);
        if (imageBase.Kind == PeSymbolKind.Undefined)
        {
            imageBase.Kind = PeSymbolKind.Defined;
            imageBase.Chunk = _imageStart;
        }
    }

    private PeSymbol GetOrAddGlobal(string name)
    {
        if (!_globals.TryGetValue(name, out var symbol))
        {
            symbol = new PeSymbol(name);
            _globals.Add(name, symbol);
        }

        return symbol;
    }

    /// <summary>Makes a symbol a root of the link: it has to be defined, and what defines it is kept.</summary>
    private PeSymbol Require(string name)
    {
        var symbol = GetOrAddGlobal(name);
        symbol.HasStrongReference = true;
        if (symbol.Kind == PeSymbolKind.Undefined)
        {
            _unresolved.Enqueue(symbol);
        }

        if (!_roots.Contains(symbol))
        {
            _roots.Add(symbol);
        }

        return symbol;
    }

    private void AddExport(string name)
    {
        if (!_exportNames.Contains(name, StringComparer.Ordinal))
        {
            _exportNames.Add(name);
            Require(name);
        }
    }

    /// <summary>
    /// Resolves undefined symbols until nothing changes any more: from static libraries, from the generated
    /// runtime support, and finally through alternate names.
    /// </summary>
    private void ResolveSymbols()
    {
        bool progress;
        do
        {
            ResolveFromArchives();
            progress = AddRuntimeSupport() || ApplyAlternateNames();
        }
        while (progress || _unresolved.Count > 0);
    }

    /// <summary>Pulls in archive members until no undefined symbol can be satisfied by an archive any more.</summary>
    private void ResolveFromArchives()
    {
        while (_unresolved.TryDequeue(out var symbol))
        {
            if (symbol.Kind != PeSymbolKind.Undefined)
            {
                continue;
            }

            foreach (var archive in _archives)
            {
                if (!archive.TryTakeMember(symbol.Name, out var memberName, out var memberData))
                {
                    continue;
                }

                var displayName = $"{archive.Name}({Path.GetFileName(memberName)})";
                if (CoffObject.IsImportObject(memberData.Span))
                {
                    throw new LinkerException(
                        $"{displayName}: import libraries are not supported. Symbols of system DLLs are bound through Ina's import table.");
                }

                _logger.LogTrace("Loading {Member} for {Symbol}", displayName, symbol.Name);
                AddObject(CoffObject.Parse(displayName, memberData));
                break;
            }
        }
    }

    /// <summary>Adds the generated objects that define symbols which are still undefined.</summary>
    private bool AddRuntimeSupport()
    {
        var added = false;
        foreach (var (name, runtimeObject) in _runtimeSupport.ToList())
        {
            if (!_runtimeSupport.ContainsKey(name) || !_globals.TryGetValue(name, out var symbol) || symbol is not { Kind: PeSymbolKind.Undefined, HasStrongReference: true })
            {
                continue;
            }

            foreach (var defined in runtimeObject.Defines)
            {
                _runtimeSupport.Remove(defined);
            }

            _logger.LogTrace("Adding {Object} for {Symbol}", runtimeObject.Name, name);
            AddObject(CoffObject.Parse(runtimeObject.Name, runtimeObject.Build()));
            added = true;
        }

        return added;
    }

    /// <summary>
    /// Lets another symbol stand in for symbols that nothing defines: the target of an <c>/alternatename</c> option,
    /// or a function of the C runtime DLL for names that the static C runtime would provide.
    /// </summary>
    private bool ApplyAlternateNames()
    {
        var applied = false;
        foreach (var symbol in _globals.Values.ToList())
        {
            if (symbol is not { Kind: PeSymbolKind.Undefined, Alias: null, HasStrongReference: true } || IsImportable(symbol.Name))
            {
                continue;
            }

            if (!_alternateNames.TryGetValue(symbol.Name, out var alternate) && !PeTarget.RuntimeAliases.TryGetValue(symbol.Name, out alternate))
            {
                continue;
            }

            var target = GetOrAddGlobal(alternate);
            symbol.Alias = target;
            target.HasStrongReference = true;
            if (target.Kind == PeSymbolKind.Undefined)
            {
                _unresolved.Enqueue(target);
            }

            applied = true;
        }

        return applied;
    }

    private bool IsImportable(string name) =>
        _options.Imports.TryGet(name, out _)
        || (name.StartsWith(ImportPrefix, StringComparison.Ordinal) && _options.Imports.TryGet(name[ImportPrefix.Length..], out _));

    private void AddObject(CoffObject file)
    {
        if (file.Machine != _target.Machine)
        {
            throw new LinkerException($"{file.Name}: object file was built for a different architecture (machine 0x{file.Machine:x})");
        }

        _objects.Add(file);
        SelectComdatSections(file);

        var records = file.Records;
        var symbols = new PeSymbol[records.Length];
        for (var i = 0; i < symbols.Length; i++)
        {
            ref readonly var record = ref records[i];
            symbols[i] = record switch
            {
                { IsAuxiliary: true } => new PeSymbol(string.Empty) { IsLocal = true },
                { IsExternal: true } => ResolveExternal(file, record),
                { IsWeakExternal: true } => GetOrAddGlobal(record.Name),
                _ => CreateLocalSymbol(file, record),
            };
        }

        // The default of a weak external may follow it in the symbol table, hence the second pass.
        for (var i = 0; i < symbols.Length; i++)
        {
            ref readonly var record = ref records[i];
            if (record.IsWeakExternal && symbols[i] is { Kind: PeSymbolKind.Undefined, Alias: null } weak
                && (uint)record.WeakDefaultIndex < (uint)symbols.Length && !ReferenceEquals(symbols[record.WeakDefaultIndex], weak))
            {
                weak.Alias = symbols[record.WeakDefaultIndex];
            }
        }

        file.Symbols = symbols;
        foreach (var directive in file.Directives)
        {
            ApplyDirective(file, directive);
        }
    }

    /// <summary>
    /// Decides which COMDAT sections of a new object are kept. The first section with a given COMDAT symbol wins;
    /// sections that are associated with a dropped section are dropped with it.
    /// </summary>
    private void SelectComdatSections(CoffObject file)
    {
        foreach (var section in file.Sections)
        {
            if (section is not { IsComdat: true, ComdatLeader: >= 0 } || section.ComdatSelection == CoffComdat.Associative)
            {
                continue;
            }

            ref readonly var leader = ref file.Records[section.ComdatLeader];
            if (!leader.IsExternal || !_globals.TryGetValue(leader.Name, out var existing) || existing.Kind != PeSymbolKind.Defined)
            {
                continue;
            }

            if (section.ComdatSelection == CoffComdat.NoDuplicates)
            {
                ReportDuplicate(leader.Name, existing, file);
            }
            else if (section.ComdatSelection == CoffComdat.Largest && existing.Chunk is CoffSection chosen && section.Size > chosen.Size)
            {
                // The new copy replaces the one chosen so far; the symbol is defined again below.
                chosen.Discard();
                existing.Kind = PeSymbolKind.Undefined;
                existing.Chunk = null;
            }
            else
            {
                section.Discard();
            }
        }

        foreach (var section in file.Sections)
        {
            if (section is not { ComdatSelection: CoffComdat.Associative })
            {
                continue;
            }

            var parent = (uint)section.AssociatedSection < (uint)file.Sections.Length ? file.Sections[section.AssociatedSection] : null;
            if (parent is null || parent.IsDiscarded || ReferenceEquals(parent, section))
            {
                // What the section belongs to is not part of the link (debug information, a dropped duplicate).
                section.Discard();
            }
            else
            {
                parent.AddChild(section);
            }
        }
    }

    private static PeSymbol CreateLocalSymbol(CoffObject file, in CoffSymbolRecord record)
    {
        var symbol = new PeSymbol(record.Name) { IsLocal = true, File = file };
        if (record.SectionNumber > 0 && file.Sections[record.SectionNumber] is { IsDiscarded: false } section)
        {
            symbol.Kind = PeSymbolKind.Defined;
            symbol.Chunk = section;
            symbol.Value = record.Value;
        }
        else if (record.IsAbsolute)
        {
            symbol.Kind = PeSymbolKind.Absolute;
            symbol.Value = record.Value;
        }

        // Otherwise the symbol is defined in a section that is not part of the link and resolves to 0.
        return symbol;
    }

    private PeSymbol ResolveExternal(CoffObject file, in CoffSymbolRecord record)
    {
        var symbol = GetOrAddGlobal(record.Name);
        if (record.SectionNumber > 0)
        {
            if (file.Sections[record.SectionNumber] is not { IsDiscarded: false } section)
            {
                // Defined in a dropped duplicate of a COMDAT section: the copy that was kept defines the symbol.
                return symbol;
            }

            if (symbol.Kind is PeSymbolKind.Undefined or PeSymbolKind.Common)
            {
                symbol.Kind = PeSymbolKind.Defined;
                symbol.Chunk = section;
                symbol.Value = record.Value;
                symbol.File = file;
            }
            else if (!section.IsComdat)
            {
                ReportDuplicate(record.Name, symbol, file);
            }
        }
        else if (record.IsAbsolute)
        {
            if (symbol.Kind is PeSymbolKind.Undefined or PeSymbolKind.Common)
            {
                symbol.Kind = PeSymbolKind.Absolute;
                symbol.Value = record.Value;
                symbol.File = file;
            }
            else if (symbol.Kind != PeSymbolKind.Absolute || symbol.Value != record.Value)
            {
                ReportDuplicate(record.Name, symbol, file);
            }
        }
        else if (record.IsCommon)
        {
            if (symbol.Kind == PeSymbolKind.Undefined)
            {
                symbol.Kind = PeSymbolKind.Common;
                symbol.File = file;
            }

            if (symbol.Kind == PeSymbolKind.Common)
            {
                symbol.CommonSize = Math.Max(symbol.CommonSize, record.Value);
            }
        }
        else
        {
            symbol.HasStrongReference = true;
            if (symbol.Kind == PeSymbolKind.Undefined)
            {
                _unresolved.Enqueue(symbol);
            }
        }

        return symbol;
    }

    private void ReportDuplicate(string name, PeSymbol existing, CoffObject file) =>
        _errors.Add($"duplicate symbol: {name}{Environment.NewLine}>>> defined in {existing.File?.Name}{Environment.NewLine}>>> defined in {file.Name}");

    /// <summary>Handles a linker option that an object carries in its <c>.drectve</c> section.</summary>
    private void ApplyDirective(CoffObject file, string directive)
    {
        if (directive.Length < 2 || directive[0] is not ('/' or '-'))
        {
            return;
        }

        var separator = directive.IndexOf(':');
        var name = (separator < 0 ? directive[1..] : directive[1..separator]).ToLowerInvariant();
        var value = separator < 0 ? string.Empty : directive[(separator + 1)..];
        switch (name)
        {
            case "merge" when value.Split('=') is [var from, var to]:
                _merges[from] = to;
                break;

            case "alternatename" when value.Split('=') is [var symbol, var alternate]:
                _alternateNames.TryAdd(symbol, alternate);
                break;

            case "include":
                Require(value);
                break;

            case "export":
                // "name", "name,DATA" or "name=internal"; only the plain forms occur in the runtime.
                var export = value.Split(',')[0];
                if (export.Contains('='))
                {
                    _errors.Add($"{file.Name}: unsupported linker option {directive}");
                }
                else
                {
                    AddExport(export);
                }

                break;

            default:
                // Requests for the default libraries of the C++ compiler and consistency checks between objects:
                // the libraries are replaced by the import table and the generated runtime support.
                _logger.LogTrace("{File}: ignoring linker option {Option}", file.Name, directive);
                break;
        }
    }

    /// <summary>Gives every common symbol that never got a real definition its own zero-initialized storage.</summary>
    private void AllocateCommonSymbols()
    {
        foreach (var symbol in _globals.Values)
        {
            if (symbol.Kind != PeSymbolKind.Common)
            {
                continue;
            }

            // The object does not say how the variable is aligned; its size is the best hint there is.
            var alignment = Math.Min(System.Numerics.BitOperations.RoundUpToPowerOf2(Math.Max(symbol.CommonSize, 1)), 32);
            var chunk = new PeGeneratedChunk(
                ".bss", CoffSectionFlags.UninitializedData | CoffSectionFlags.Read | CoffSectionFlags.Write, alignment, symbol.CommonSize);
            _generatedChunks.Add(chunk);
            symbol.Kind = PeSymbolKind.Defined;
            symbol.Chunk = chunk;
            symbol.Value = 0;
        }
    }

    /// <summary>
    /// Turns the symbols that are still undefined into imports where the import table knows them. <c>__imp_X</c>
    /// names the slot that holds the address of X; a plain name is a function that is called through a thunk.
    /// </summary>
    private void BindImports()
    {
        foreach (var symbol in _globals.Values)
        {
            if (symbol is not { Kind: PeSymbolKind.Undefined, Alias: null })
            {
                continue;
            }

            var isAddress = symbol.Name.StartsWith(ImportPrefix, StringComparison.Ordinal);
            var importName = isAddress ? symbol.Name[ImportPrefix.Length..] : symbol.Name;
            if (!_options.Imports.TryGet(importName, out var imported))
            {
                continue;
            }

            if (!_imports.TryGetValue((imported.Library, importName), out var import))
            {
                import = new PeImport(imported.Library, importName);
                _imports.Add((imported.Library, importName), import);
            }

            symbol.Kind = PeSymbolKind.Imported;
            symbol.Import = import;
            if (isAddress)
            {
                import.AddressSymbol = symbol;
            }
            else
            {
                import.ThunkSymbol = symbol;
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Garbage collection and relocation scanning
    // ---------------------------------------------------------------------------------------------------------------

    private IEnumerable<CoffSection> AllSections()
    {
        foreach (var file in _objects)
        {
            foreach (var section in file.Sections)
            {
                if (section is { IsDiscarded: false })
                {
                    yield return section;
                }
            }
        }
    }

    /// <summary>
    /// Marks what ends up in the image. Ordinary sections are always kept, like the Microsoft linker does; COMDAT
    /// sections, which is what compilers emit for individual functions, only when something refers to them.
    /// Associated sections (unwind data) follow the section they belong to.
    /// </summary>
    private void MarkLiveChunks()
    {
        var pending = new Stack<CoffSection>();

        void Mark(PeChunk? chunk)
        {
            if (chunk is null || chunk.IsLive)
            {
                return;
            }

            chunk.IsLive = true;
            if (chunk is not CoffSection section)
            {
                return;
            }

            pending.Push(section);
            if (section.Children is not null)
            {
                foreach (var child in section.Children)
                {
                    Mark(child);
                }
            }
        }

        foreach (var section in AllSections())
        {
            if (!_options.GarbageCollectSections || !section.IsComdat)
            {
                Mark(section);
            }
        }

        foreach (var root in _roots)
        {
            Mark(root.Target.Chunk);
        }

        // The loader finds these through the data directories, not through references.
        foreach (var name in new[] { TlsDirectorySymbol, LoadConfigurationSymbol })
        {
            if (_globals.TryGetValue(name, out var symbol))
            {
                Mark(symbol.Target.Chunk);
            }
        }

        while (pending.TryPop(out var section))
        {
            var symbols = section.File.Symbols;
            foreach (ref readonly var relocation in section.Relocations.AsSpan())
            {
                Mark(symbols[relocation.SymbolIndex].Target.Chunk);
            }
        }
    }

    /// <summary>
    /// Walks the relocations of all live sections to report undefined symbols and to find out which imports are
    /// used, and how: through their address slot only, or also through a thunk.
    /// </summary>
    private void ScanRelocations()
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
                Use(symbols[relocation.SymbolIndex], section.DisplayName);
            }
        }

        foreach (var root in _roots)
        {
            Use(root, "the linker options");
        }
    }

    private void Use(PeSymbol reference, string referencedBy)
    {
        var symbol = reference.Target;
        if (symbol.Kind == PeSymbolKind.Imported)
        {
            var import = symbol.Import!;
            import.IsUsed = true;
            import.NeedsThunk |= ReferenceEquals(import.ThunkSymbol, symbol);
        }
        else if (symbol is { Kind: PeSymbolKind.Undefined, IsLocal: false, HasStrongReference: true } && _reportedUndefined.Add(symbol))
        {
            _errors.Add($"undefined symbol: {symbol.Name}{Environment.NewLine}>>> referenced by {referencedBy}");
        }
    }
}
