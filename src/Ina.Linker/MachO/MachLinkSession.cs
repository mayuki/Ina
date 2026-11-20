using Microsoft.Extensions.Logging;

namespace Ina.Linker.MachO;

/// <summary>One invocation of the Mach-O linker. The passes run in the order of <see cref="Run"/>.</summary>
internal sealed partial class MachLinkSession
{
    /// <summary>C symbols carry a leading underscore in Mach-O object files.</summary>
    private const string SymbolPrefix = "_";

    private const string HeaderSymbolName = "__mh_execute_header";
    private const string DsoHandleSymbolName = "___dso_handle";

    /// <summary>
    /// A call to <c>_objc_msgSend$selector</c> sends the Objective-C message <c>selector</c>; the linker supplies
    /// the code that passes the selector.
    /// </summary>
    private const string ObjCStubPrefix = MachTarget.ObjCMessageSend + "$";

    private const uint CodeSectionFlags = MachSectionType.AttrPureInstructions | MachSectionType.AttrSomeInstructions;

    private readonly LinkOptions _options;
    private readonly ILogger _logger;
    private readonly MachTarget _target;
    private readonly MachLinkContext _context;
    private readonly string _entrySymbolName;

    private readonly List<MachObjectFile> _objects = [];
    private readonly List<ArchiveFile> _archives = [];
    private readonly Dictionary<string, MachSymbol> _globals = new(StringComparer.Ordinal);
    private readonly Queue<MachSymbol> _unresolved = new();
    private readonly List<string> _errors = [];
    private readonly HashSet<MachSymbol> _reportedUndefined = [];

    /// <summary>Hosts atoms the linker creates itself, such as the storage of common symbols.</summary>
    private readonly MachObjectFile _internalObject;

    private readonly List<MachSymbol> _linkerSymbols = [];
    private readonly List<MachSymbol> _imports = [];
    private readonly List<MachSymbol> _exports = [];
    private readonly List<UnwindRecord> _unwindRecords = [];
    private Dictionary<(string Segment, string Section), List<Atom>>? _atomsByOutputSection;
    private int _atomCount;
    private int _liveAtomCount;

    public MachLinkSession(LinkOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
        _target = MachTarget.Create(options.Architecture);
        _context = new MachLinkContext(options, _target);
        _entrySymbolName = SymbolPrefix + options.MainSymbol;
        _internalObject = MachObjectFile.CreateSynthetic("<ina internal>", _target.CpuType);
    }

    public LinkResult Run()
    {
        LoadInputs();
        RequestExportedSymbols();
        ResolveFromArchives();
        AddGeneratedFunctions();
        DefineLinkerSymbols();
        AllocateCommonSymbols();
        MarkLiveAtoms();
        ScanRelocations();
        ThrowIfErrors();

        CreateOutputSections();
        AssignAddresses();
        FinalizeLinkerSymbols();
        return WriteOutput();
    }

    /// <summary>All atoms in a deterministic order: by object file, section and address, then the linker's own.</summary>
    private IEnumerable<Atom> AllAtoms()
    {
        foreach (var file in _objects)
        {
            foreach (var section in file.Sections)
            {
                foreach (var atom in section.Atoms)
                {
                    yield return atom;
                }
            }
        }

        foreach (var section in _internalObject.Sections)
        {
            foreach (var atom in section.Atoms)
            {
                yield return atom;
            }
        }
    }

    private IEnumerable<Atom> LiveAtoms() => AllAtoms().Where(static atom => atom.IsLive);

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
            if (ArchiveFile.IsArchive(data))
            {
                _archives.Add(ArchiveFile.Parse(Path.GetFileName(input.Name), input.Data, DefinedSymbolsOfMember));
            }
            else if (MachObjectFile.IsMachObject(data))
            {
                AddObject(MachObjectFile.Parse(Path.GetFileName(input.Name), input.Data));
            }
            else
            {
                throw new LinkerException($"{input.Name}: unknown file format (expected a Mach-O object or an ar archive)");
            }
        }
    }

    private static IEnumerable<string>? DefinedSymbolsOfMember(string memberName, ReadOnlyMemory<byte> data)
    {
        if (!MachObjectFile.IsMachObject(data.Span))
        {
            return null;
        }

        return MachObjectFile.Parse(memberName, data).Nlist
            .Where(static symbol => !symbol.IsStab && symbol.IsExternal && !symbol.IsUndefined && !symbol.IsCommon)
            .Select(static symbol => symbol.Name);
    }

    /// <summary>
    /// Asks for the symbols that are to be exported, so that the archive members that define them are loaded even
    /// if nothing refers to them, like <c>-u symbol</c> does. Since .NET 11 the descriptor that diagnostic tools
    /// look for lives in such a member. A symbol that no input defines is not an error; it is simply not exported.
    /// </summary>
    private void RequestExportedSymbols()
    {
        foreach (var exported in _options.ExportedSymbols)
        {
            var name = SymbolPrefix + exported;
            if (!_globals.TryGetValue(name, out var symbol))
            {
                symbol = new MachSymbol(name);
                _globals.Add(name, symbol);
            }

            if (symbol.Kind == MachSymbolKind.Undefined)
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
            if (symbol.Kind != MachSymbolKind.Undefined)
            {
                continue;
            }

            foreach (var archive in _archives)
            {
                if (archive.TryTakeMember(symbol.Name, out var memberName, out var memberData))
                {
                    _logger.LogTrace("Loading {Archive}({Member}) for {Symbol}", archive.Name, memberName, symbol.Name);
                    AddObject(MachObjectFile.Parse($"{archive.Name}({memberName})", memberData));
                    break;
                }
            }
        }
    }

    private void AddObject(MachObjectFile file)
    {
        if (file.CpuType != _target.CpuType)
        {
            throw new LinkerException($"{file.Name}: object file was built for a different architecture (expected {_target.Name})");
        }

        _objects.Add(file);
        var nlist = file.Nlist;
        var symbols = new MachSymbol[nlist.Count];
        for (var i = 0; i < symbols.Length; i++)
        {
            var entry = nlist[i];
            symbols[i] = entry.IsStab || !entry.IsExternal ? CreateLocalSymbol(file, entry) : ResolveGlobalSymbol(file, entry);
        }

        file.Symbols = symbols;
    }

    /// <summary>Finds the atom a symbol is defined in; null when its section does not take part in the link.</summary>
    private static Atom? DefiningAtom(MachObjectFile file, in MachNlist entry)
    {
        if (!entry.IsDefinedInSection || entry.SectionNumber == 0 || entry.SectionNumber > file.Sections.Length)
        {
            return null;
        }

        var section = file.Sections[entry.SectionNumber - 1];
        return section.Kind == MachSectionKind.Regular ? section.FindAtom(entry.Value) : null;
    }

    private static MachSymbol CreateLocalSymbol(MachObjectFile file, in MachNlist entry)
    {
        var symbol = new MachSymbol(entry.Name) { IsLocal = true, File = file };
        if (entry.IsStab)
        {
            return symbol;
        }

        if (DefiningAtom(file, entry) is { } atom)
        {
            symbol.Kind = MachSymbolKind.Defined;
            symbol.Atom = atom;
            symbol.Value = entry.Value - atom.InputAddress;
        }
        else if (entry.IsAbsolute)
        {
            symbol.Kind = MachSymbolKind.Absolute;
            symbol.Value = entry.Value;
        }

        // Otherwise the symbol belongs to a section that is not linked (debug information); it resolves to 0.
        return symbol;
    }

    private MachSymbol ResolveGlobalSymbol(MachObjectFile file, in MachNlist entry)
    {
        if (entry.IsIndirect)
        {
            throw new LinkerException($"{file.Name}: '{entry.Name}' is an alias of another symbol, which is not supported");
        }

        var atom = DefiningAtom(file, entry);
        if (entry.IsDefinedInSection && atom is null)
        {
            // Defined in a section that is not linked as such, e.g. a label inside the unwind tables. Nothing
            // outside of that section can refer to it.
            return new MachSymbol(entry.Name) { IsLocal = true, File = file };
        }

        if (!_globals.TryGetValue(entry.Name, out var symbol))
        {
            symbol = new MachSymbol(entry.Name);
            _globals.Add(entry.Name, symbol);
        }

        if (entry.IsUndefined)
        {
            if (!entry.IsWeakReference)
            {
                symbol.HasStrongReference = true;
            }

            if (symbol.Kind == MachSymbolKind.Undefined)
            {
                _unresolved.Enqueue(symbol);
            }

            return symbol;
        }

        var isWeak = entry.IsWeakDefinition;
        switch (symbol.Kind)
        {
            case MachSymbolKind.Undefined:
                Define(symbol, file, entry, atom);
                break;

            case MachSymbolKind.Common:
                if (entry.IsCommon)
                {
                    symbol.Size = Math.Max(symbol.Size, entry.Value);
                    symbol.CommonAlignment = Math.Max(symbol.CommonAlignment, entry.CommonAlignment);
                }
                else if (!isWeak)
                {
                    Define(symbol, file, entry, atom);
                }

                break;

            default:
                if (entry.IsCommon)
                {
                    break;
                }

                if (symbol.IsWeakDefinition && !isWeak)
                {
                    Define(symbol, file, entry, atom);
                }
                else if (!symbol.IsWeakDefinition && !isWeak)
                {
                    _errors.Add($"duplicate symbol: {entry.Name}{Environment.NewLine}>>> defined in {symbol.File?.Name}{Environment.NewLine}>>> defined in {file.Name}");
                }

                break;
        }

        return symbol;
    }

    private static void Define(MachSymbol symbol, MachObjectFile file, in MachNlist entry, Atom? atom)
    {
        symbol.File = file;
        symbol.IsWeakDefinition = entry.IsWeakDefinition;
        symbol.IsPrivateExternal = entry.IsPrivateExternal;
        symbol.Atom = null;
        if (entry.IsCommon)
        {
            symbol.Kind = MachSymbolKind.Common;
            symbol.Size = entry.Value;
            symbol.CommonAlignment = entry.CommonAlignment;
            symbol.Value = 0;
        }
        else if (atom is not null)
        {
            symbol.Kind = MachSymbolKind.Defined;
            symbol.Atom = atom;
            symbol.Value = entry.Value - atom.InputAddress;
        }
        else
        {
            symbol.Kind = MachSymbolKind.Absolute;
            symbol.Value = entry.Value;
        }
    }

    /// <summary>
    /// Supplies the code that Apple's toolchain contributes to a link without being asked: stubs for Objective-C
    /// message sends and functions of the compiler support library, for the names that are still undefined. Inputs
    /// that define these names themselves win because they were resolved earlier.
    /// </summary>
    private void AddGeneratedFunctions()
    {
        foreach (var symbol in _globals.Values.Where(static symbol => symbol.Kind == MachSymbolKind.Undefined).ToList())
        {
            if (symbol.Kind != MachSymbolKind.Undefined)
            {
                // Defined in the meantime, because another generated function needed it.
                continue;
            }

            if (symbol.Name.StartsWith(ObjCStubPrefix, StringComparison.Ordinal))
            {
                DefineObjCStub(symbol, symbol.Name[ObjCStubPrefix.Length..]);
            }
            else if (_target.CreateSupportFunction(symbol.Name) is { } function)
            {
                DefineGeneratedFunction(symbol, function, "__text", selectorReference: null);
            }
        }
    }

    /// <summary>
    /// Creates what a compiler emits for a message send with an explicit selector: the name of the selector, a
    /// pointer to it that the Objective-C runtime replaces by the registered selector when the image is loaded,
    /// and the stub that passes that selector on.
    /// </summary>
    private void DefineObjCStub(MachSymbol symbol, string selector)
    {
        var name = _internalObject.AddSyntheticSection(
            MachSectionLayout.TextSegment, "__objc_methname", (ulong)System.Text.Encoding.UTF8.GetByteCount(selector) + 1, 1,
            MachSectionType.CStringLiterals, System.Text.Encoding.UTF8.GetBytes(selector + '\0'));
        var reference = _internalObject.AddSyntheticSection(
            MachSectionLayout.DataSegment, "__objc_selrefs", 8, 8, MachSectionType.LiteralPointers | MachSectionType.AttrNoDeadStrip, new byte[8]);
        reference.Relocations = [new MachRelocation(0, MachRelocKind.Pointer, 3, _internalObject.AddSyntheticSymbol(AnchorOf(name)), 0)];

        DefineGeneratedFunction(symbol, _target.CreateObjCStub(), "__objc_stubs", AnchorOf(reference));
    }

    private MachSymbol AnchorOf(Atom atom) => new(string.Empty) { IsLocal = true, Kind = MachSymbolKind.Defined, File = _internalObject, Atom = atom };

    private void DefineGeneratedFunction(MachSymbol symbol, MachSyntheticFunction function, string sectionName, MachSymbol? selectorReference)
    {
        var atom = _internalObject.AddSyntheticSection(
            MachSectionLayout.TextSegment, sectionName, (ulong)function.Code.Length, _target.FunctionAlignment, CodeSectionFlags, function.Code);
        symbol.Kind = MachSymbolKind.Defined;
        symbol.File = _internalObject;
        symbol.Atom = atom;
        symbol.Value = 0;
        symbol.IsPrivateExternal = true;

        atom.Relocations = function.Relocations
            .Select(relocation => new MachRelocation(
                relocation.Offset, relocation.Kind, 2,
                _internalObject.AddSyntheticSymbol(relocation.Symbol == MachTarget.SelectorReference ? selectorReference! : ReferenceFromGeneratedCode(relocation.Symbol)),
                0))
            .ToArray();
        if (function.UnwindEncoding != 0)
        {
            atom.UnwindRecords = [new UnwindRecord { Function = atom, Length = (uint)function.Code.Length, Encoding = function.UnwindEncoding }];
        }
    }

    /// <summary>Resolves a symbol that generated code refers to, generating that function as well if it is one of the linker's.</summary>
    private MachSymbol ReferenceFromGeneratedCode(string name)
    {
        if (!_globals.TryGetValue(name, out var symbol))
        {
            symbol = new MachSymbol(name);
            _globals.Add(name, symbol);
        }

        symbol.HasStrongReference = true;
        if (symbol.Kind == MachSymbolKind.Undefined && _target.CreateSupportFunction(name) is { } function)
        {
            DefineGeneratedFunction(symbol, function, "__text", selectorReference: null);
        }

        return symbol;
    }

    /// <summary>Claims the symbols that the linker itself provides, so that they are no longer undefined.</summary>
    private void DefineLinkerSymbols()
    {
        foreach (var symbol in _globals.Values)
        {
            if (symbol.Kind != MachSymbolKind.Undefined)
            {
                continue;
            }

            if (symbol.Name is HeaderSymbolName or DsoHandleSymbolName)
            {
                symbol.Kind = MachSymbolKind.Synthetic;
                symbol.IsPrivateExternal = true;
                _linkerSymbols.Add(symbol);
            }
            else if (ParseBoundary(symbol.Name) is { } boundary)
            {
                symbol.Kind = MachSymbolKind.Synthetic;
                symbol.IsPrivateExternal = true;
                symbol.Boundary = boundary;
                _linkerSymbols.Add(symbol);
            }
        }

        foreach (var name in _options.ExportedSymbols)
        {
            if (_globals.TryGetValue(SymbolPrefix + name, out var symbol) && symbol.Kind == MachSymbolKind.Defined)
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

    /// <summary>
    /// Recognizes <c>section$start$SEG$sect</c>, <c>section$end$SEG$sect</c>, <c>segment$start$SEG</c> and
    /// <c>segment$end$SEG</c>, through which code finds the bounds of a section or segment of its own image.
    /// </summary>
    private static MachBoundary? ParseBoundary(string name)
    {
        var parts = name.Split('$');
        return parts switch
        {
            ["section", "start" or "end", var segment, var section] => new MachBoundary(segment, section, parts[1] == "end"),
            ["segment", "start" or "end", var segment] => new MachBoundary(segment, null, parts[1] == "end"),
            _ => null,
        };
    }

    /// <summary>Gives every common symbol that never got a real definition its own zero-initialized storage.</summary>
    private void AllocateCommonSymbols()
    {
        foreach (var symbol in _globals.Values)
        {
            if (symbol.Kind != MachSymbolKind.Common)
            {
                continue;
            }

            var atom = _internalObject.AddSyntheticSection(
                MachSectionLayout.DataSegment, "__common", symbol.Size, Math.Max(symbol.CommonAlignment, 1), MachSectionType.ZeroFill,
                ReadOnlyMemory<byte>.Empty);
            symbol.Kind = MachSymbolKind.Defined;
            symbol.File = _internalObject;
            symbol.Atom = atom;
            symbol.Value = 0;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Dead stripping and relocation scanning
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Marks everything reachable from the entry point. Unwind records do not keep code alive; they stay with the
    /// code they describe and then keep their own references (language specific data, personality) alive.
    /// </summary>
    private void MarkLiveAtoms()
    {
        var pending = new Stack<Atom>();

        void Mark(Atom atom)
        {
            if (!atom.IsLive)
            {
                atom.IsLive = true;
                pending.Push(atom);
            }
        }

        void MarkSymbol(MachSymbol symbol)
        {
            if (symbol.Kind == MachSymbolKind.Defined && symbol.Atom is { } atom)
            {
                Mark(atom);
            }
            else if (symbol.Boundary is { } boundary)
            {
                // Whoever asks for the bounds of a section iterates over its content, so all of it stays.
                foreach (var member in AtomsOf(boundary))
                {
                    Mark(member);
                }
            }
        }

        if (!_globals.TryGetValue(_entrySymbolName, out var entry) || entry.Kind != MachSymbolKind.Defined)
        {
            _errors.Add($"undefined symbol: {_entrySymbolName}{Environment.NewLine}>>> the entry point of the program");
        }
        else
        {
            MarkSymbol(entry);
        }

        foreach (var symbol in _exports)
        {
            MarkSymbol(symbol);
        }

        foreach (var atom in AllAtoms())
        {
            _atomCount++;
            if (!_options.GarbageCollectSections || atom.IsNoDeadStrip || IsRootSection(atom.Section))
            {
                Mark(atom);
            }
        }

        while (pending.TryPop(out var atom))
        {
            _liveAtomCount++;
            var symbols = atom.File.Symbols;
            foreach (ref readonly var relocation in atom.Relocations.AsSpan())
            {
                MarkSymbol(symbols[relocation.SymbolIndex]);
                if (relocation.SubtrahendIndex >= 0)
                {
                    MarkSymbol(symbols[relocation.SubtrahendIndex]);
                }
            }

            if (atom.UnwindRecords is not { } records)
            {
                continue;
            }

            foreach (var record in records)
            {
                if ((record.Lsda ?? record.Fde?.Lsda) is { } lsda)
                {
                    Mark(lsda);
                }

                if (PersonalityOf(record) is { } personality)
                {
                    MarkSymbol(personality);
                }
            }
        }
    }

    /// <summary>The personality function of the code a record describes, from the record itself or from its CIE.</summary>
    private static MachSymbol? PersonalityOf(UnwindRecord record)
    {
        var index = record.Fde is { } fde ? fde.Cie.PersonalityIndex : record.PersonalityIndex;
        return index >= 0 ? record.Function.File.Symbols[index] : null;
    }

    /// <summary>Initializers and terminators run without being referenced by anything.</summary>
    private static bool IsRootSection(MachInputSection section) =>
        section.Type is MachSectionType.ModInitFuncPointers or MachSectionType.ModTermFuncPointers or MachSectionType.InitFuncOffsets
            or MachSectionType.ThreadLocalInitFunctionPointers;

    private List<Atom> AtomsOf(MachBoundary boundary)
    {
        if (_atomsByOutputSection is null)
        {
            _atomsByOutputSection = [];
            foreach (var atom in AllAtoms())
            {
                var key = MachSectionLayout.OutputName(atom.Section.SegmentName, atom.Section.SectionName);
                if (!_atomsByOutputSection.TryGetValue(key, out var list))
                {
                    _atomsByOutputSection[key] = list = [];
                }

                list.Add(atom);
            }
        }

        if (boundary.Section is { } section)
        {
            return _atomsByOutputSection.GetValueOrDefault((boundary.Segment, section)) ?? [];
        }

        return _atomsByOutputSection.Where(pair => pair.Key.Segment == boundary.Segment).SelectMany(static pair => pair.Value).ToList();
    }

    /// <summary>
    /// Walks the relocations of all live atoms to find out which symbols are imported and which GOT entries, stubs
    /// and load-time fixups are needed. Their number determines the size of the generated sections.
    /// </summary>
    private void ScanRelocations()
    {
        foreach (var atom in LiveAtoms())
        {
            var symbols = atom.File.Symbols;
            foreach (ref readonly var relocation in atom.Relocations.AsSpan())
            {
                ClassifyUndefined(symbols[relocation.SymbolIndex], atom);
                if (relocation.SubtrahendIndex >= 0)
                {
                    ClassifyUndefined(symbols[relocation.SubtrahendIndex], atom);
                }

                try
                {
                    _target.Scan(_context, atom, relocation);
                }
                catch (LinkerException e)
                {
                    _errors.Add(e.Message);
                }
            }

            if (atom.UnwindRecords is not { } records)
            {
                continue;
            }

            foreach (var record in records)
            {
                _unwindRecords.Add(record);
                if (PersonalityOf(record) is { } personality)
                {
                    // The unwind tables refer to a personality function through its GOT entry.
                    ClassifyUndefined(personality, atom);
                    _context.AddGotEntry(personality);
                }
            }
        }
    }

    /// <summary>Decides what a symbol without a definition is: an import, an unresolved weak symbol, or an error.</summary>
    private void ClassifyUndefined(MachSymbol symbol, Atom referencedBy)
    {
        if (symbol.Kind != MachSymbolKind.Undefined || symbol.IsLocal)
        {
            return;
        }

        if (_options.Imports.TryGet(symbol.Name, out var import))
        {
            symbol.Kind = MachSymbolKind.Imported;
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
