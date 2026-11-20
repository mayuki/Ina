
namespace Ina.Linker.Pe;

/// <summary>A function whose machine code the linker generates itself.</summary>
/// <param name="Relocations">Relocations against symbol names; the addend is whatever <paramref name="Code"/> holds.</param>
internal sealed record PeFunction(string Name, byte[] Code, (uint Offset, ushort Type, string Symbol)[] Relocations)
{
    /// <summary>
    /// How to unwind the stack frame of the function, in the format of the architecture. Null for leaf functions,
    /// which do not move the stack pointer and need no description.
    /// </summary>
    public byte[]? UnwindData { get; init; }
}

/// <summary>
/// An object that the linker contributes when one of its symbols is needed. Together these objects stand in for
/// the parts of the Visual C++ runtime that a native toolchain links statically into every program.
/// </summary>
internal sealed record PeRuntimeObject(string Name, string[] Defines, Func<byte[]> Build);

/// <summary>The architecture specific parts of the PE linker.</summary>
internal abstract class PeTarget
{
    /// <summary>
    /// The initial value of the stack protection cookie. Windows replaces exactly this value with a random one
    /// when it loads an image whose load configuration points at the cookie.
    /// </summary>
    private const ulong DefaultSecurityCookie = 0x0000_2b99_2ddf_a232;

    /// <summary>Size of the load configuration directory that current Microsoft linkers emit for 64-bit images.</summary>
    private const int LoadConfigurationSize = 0x140;

    private const int LoadConfigurationSecurityCookieOffset = 0x58;

    public abstract ushort Machine { get; }

    /// <summary>Size of a thunk that jumps to an imported function.</summary>
    public abstract uint ThunkSize { get; }

    /// <summary>Size of an entry of the exception directory (<c>.pdata</c>).</summary>
    public abstract uint RuntimeFunctionSize { get; }

    /// <summary>The relocation that stores a 64-bit address, which the loader has to adjust when the image moves.</summary>
    public abstract ushort Addr64Relocation { get; }

    /// <summary>Writes a thunk that jumps to the address stored in a slot of the import address table.</summary>
    public abstract void WriteThunk(Span<byte> code, uint thunkRva, uint slotRva);

    /// <summary>Patches <paramref name="data"/> (the content of <paramref name="section"/>) for one relocation.</summary>
    public abstract void Apply(PeLinkContext context, CoffSection section, Span<byte> data, in CoffRelocation relocation, PeSymbol symbol);

    public abstract string RelocationName(ushort type);

    /// <summary>The process entry point <c>wmainCRTStartup</c>, which prepares the C runtime and calls <c>wmain</c>.</summary>
    protected abstract PeFunction CreateEntryPoint(PeLinkOptions options);

    /// <summary>
    /// The TLS callback that runs the dynamic initializers of <c>thread_local</c> variables for a new thread, and
    /// the function that compiled code calls to run them on first use.
    /// </summary>
    protected abstract PeFunction[] CreateThreadLocalInitializers();

    /// <summary>The functions behind the stack protection of the C++ compiler (<c>/GS</c>).</summary>
    protected abstract PeFunction[] CreateStackProtection();

    /// <summary>The stack probe that compiled code calls before it allocates a large stack frame.</summary>
    protected abstract PeFunction CreateStackProbe();

    /// <summary>
    /// The targets of the Control Flow Guard function pointers of an image that does not enable the guard: a check
    /// that accepts every target, and where it exists a dispatcher that just jumps.
    /// </summary>
    protected abstract (string Pointer, PeFunction Function)[] CreateControlFlowGuardStubs();

    /// <summary>
    /// Further functions of the static C runtime that compiled code for the architecture calls. Each one becomes
    /// an object of its own, so that only the ones that are used end up in the image.
    /// </summary>
    protected virtual IEnumerable<PeFunction> CreateAdditionalFunctions() => [];

    /// <summary>Adds the unwind data of a function to the object under construction.</summary>
    protected abstract void AddUnwindData(CoffObjectBuilder builder, CoffObjectBuilder.Section text, PeFunction function);

    public static PeTarget Create(TargetArchitecture architecture) => architecture switch
    {
        TargetArchitecture.X64 => new PeX64Target(),
        TargetArchitecture.Arm64 => new PeArm64Target(),
        _ => throw new LinkerException($"unsupported target architecture for Windows: {architecture}"),
    };

    /// <summary>
    /// Symbols that are satisfied by another symbol when nothing defines them. The first group maps the C++
    /// allocation functions to the C heap; the second is what <c>oldnames.lib</c> does for the POSIX names of
    /// functions that the C runtime exports with a leading underscore.
    /// </summary>
    public static IReadOnlyDictionary<string, string> RuntimeAliases { get; } = CreateRuntimeAliases();

    private static Dictionary<string, string> CreateRuntimeAliases()
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["??2@YAPEAX_KAEBUnothrow_t@std@@@Z"] = "malloc",  // operator new(size_t, const std::nothrow_t&)
            ["??_U@YAPEAX_KAEBUnothrow_t@std@@@Z"] = "malloc", // operator new[](size_t, const std::nothrow_t&)
            ["??3@YAXPEAX@Z"] = "free",                         // operator delete(void*)
            ["??3@YAXPEAX_K@Z"] = "free",                       // operator delete(void*, size_t)
            ["??_V@YAXPEAX@Z"] = "free",                        // operator delete[](void*)
            ["??_V@YAXPEAX_K@Z"] = "free",                      // operator delete[](void*, size_t)

            // An executable that uses the C runtime DLL registers its exit handlers with the DLL.
            ["atexit"] = "_crt_atexit",
        };

        string[] oldNames =
        [
            "access", "chdir", "chmod", "chsize", "close", "creat", "cwait", "dup", "dup2", "ecvt", "eof", "execl", "execle", "execlp", "execlpe",
            "execv", "execve", "execvp", "execvpe", "fcloseall", "fcvt", "fdopen", "fgetchar", "filelength", "fileno", "flushall", "fputchar",
            "gcvt", "getch", "getche", "getcwd", "getpid", "getw", "isatty", "itoa", "kbhit", "lfind", "locking", "lsearch", "lseek", "ltoa",
            "memccpy", "memicmp", "mkdir", "mktemp", "open", "putch", "putenv", "putw", "read", "rmdir", "rmtmp", "setmode", "sopen", "spawnl",
            "spawnle", "spawnlp", "spawnlpe", "spawnv", "spawnve", "spawnvp", "spawnvpe", "strcmpi", "strdup", "stricmp", "strlwr", "strnicmp",
            "strnset", "strrev", "strset", "strupr", "swab", "tell", "tempnam", "tzset", "ultoa", "umask", "ungetch", "unlink", "wcsdup",
            "wcsicmp", "wcsicoll", "wcslwr", "wcsnicmp", "wcsnset", "wcsrev", "wcsset", "wcsupr", "write",
        ];
        foreach (var name in oldNames)
        {
            aliases[name] = "_" + name;
        }

        return aliases;
    }

    /// <summary>Creates the objects that replace the statically linked parts of the Visual C++ runtime.</summary>
    public IReadOnlyList<PeRuntimeObject> CreateRuntimeSupport(PeLinkOptions options)
    {
        var entryPoint = CreateEntryPoint(options);
        var threadLocalInitializers = CreateThreadLocalInitializers();
        var stackProtection = CreateStackProtection();
        var stackProbe = CreateStackProbe();
        var guardStubs = CreateControlFlowGuardStubs();

        var objects = new List<PeRuntimeObject>
        {
            new("<ina startup>", [entryPoint.Name, "__xi_a", "__xi_z", "__xc_a", "__xc_z"], () => BuildStartup(entryPoint)),
            new("<ina tls>", ["_tls_used", "_tls_index", "_tls_start", "_tls_end", "__xl_a", "__xl_z"], BuildThreadLocalStorage),
            new(
                "<ina tls initializers>", [.. threadLocalInitializers.Select(static f => f.Name), "__tls_guard", "__xd_a", "__xd_z"],
                () => BuildThreadLocalInitializers(threadLocalInitializers)),
            new(
                "<ina stack protection>", ["__security_cookie", "_load_config_used", .. stackProtection.Select(static f => f.Name)],
                () => BuildStackProtection(stackProtection)),
            new("<ina stack probe>", [stackProbe.Name], () => BuildFunctions(stackProbe)),
            new("<ina control flow guard>", [.. guardStubs.Select(static s => s.Pointer)], () => BuildControlFlowGuard(guardStubs)),

            // Compilers reference this symbol from code that uses floating point; it only has to exist.
            new("<ina fltused>", ["_fltused"], () => BuildAbsolute("_fltused", 0x9875)),

            // std::nothrow, an empty object.
            new("<ina nothrow>", ["?nothrow@std@@3Unothrow_t@1@B"], () => BuildData("?nothrow@std@@3Unothrow_t@1@B", new byte[1], writable: false)),
        };

        foreach (var function in CreateAdditionalFunctions())
        {
            objects.Add(new($"<ina {function.Name}>", [function.Name], () => BuildFunctions(function)));
        }

        // The option words behind the inline printf and scanf families of the C runtime headers.
        foreach (var family in new[] { "printf", "scanf" })
        {
            var storage = $"?_OptionsStorage@?1??__local_stdio_{family}_options@@9@9";
            objects.Add(new($"<ina {family} options>", [storage], () => BuildData(storage, new byte[8], writable: true)));
        }

        return objects;
    }

    private byte[] BuildFunctions(params PeFunction[] functions)
    {
        var builder = new CoffObjectBuilder(Machine);
        foreach (var function in functions)
        {
            AddFunction(builder, function);
        }

        return builder.Build();
    }

    private CoffObjectBuilder.Section AddFunction(CoffObjectBuilder builder, PeFunction function, bool isExternal = true)
    {
        var text = builder.Text(".text$mn", function.Code);
        if (isExternal)
        {
            builder.External(function.Name, text);
        }
        else
        {
            builder.Static(function.Name, text);
        }

        foreach (var (offset, type, symbol) in function.Relocations)
        {
            builder.Relocate(text, offset, type, symbol);
        }

        if (function.UnwindData is not null)
        {
            AddUnwindData(builder, text, function);
        }

        return text;
    }

    private byte[] BuildStartup(PeFunction entryPoint)
    {
        var builder = new CoffObjectBuilder(Machine);
        AddFunction(builder, entryPoint);

        // The initializer tables of the C runtime: compilers put function pointers between these markers, and the
        // startup code hands the ranges to the C runtime, which skips the null markers themselves.
        foreach (var (section, symbol) in new[] { (".CRT$XIA", "__xi_a"), (".CRT$XIZ", "__xi_z"), (".CRT$XCA", "__xc_a"), (".CRT$XCZ", "__xc_z") })
        {
            builder.External(symbol, builder.ReadOnlyData(section, new byte[8], 8));
        }

        return builder.Build();
    }

    /// <summary>
    /// The TLS directory and what it points at: the bounds of the initialization image, the slot that receives
    /// the TLS index of the module, and the list of callbacks.
    /// </summary>
    private byte[] BuildThreadLocalStorage()
    {
        var builder = new CoffObjectBuilder(Machine);
        const uint Template = CoffSectionFlags.InitializedData | CoffSectionFlags.Read | CoffSectionFlags.Write;

        // Thread-local variables live in ".tls$..." sections, which sort between these two markers.
        builder.External("_tls_start", builder.AddSection(".tls", new byte[1], Template));
        builder.External("_tls_end", builder.AddSection(".tls$ZZZ", new byte[1], Template));
        builder.External("_tls_index", builder.Data(".data", new byte[4], 4));
        builder.External("__xl_a", builder.ReadOnlyData(".CRT$XLA", new byte[8], 8));
        builder.External("__xl_z", builder.ReadOnlyData(".CRT$XLZ", new byte[8], 8));

        // IMAGE_TLS_DIRECTORY64. The callbacks start after the null entry of the first marker.
        var directory = new byte[40];
        LittleEndian.U64(directory, 24, 8);
        var section = builder.ReadOnlyData(".rdata$T", directory, 8);
        builder.External("_tls_used", section);
        builder.Relocate(section, 0, Addr64Relocation, "_tls_start");
        builder.Relocate(section, 8, Addr64Relocation, "_tls_end");
        builder.Relocate(section, 16, Addr64Relocation, "_tls_index");
        builder.Relocate(section, 24, Addr64Relocation, "__xl_a");
        return builder.Build();
    }

    private byte[] BuildThreadLocalInitializers(PeFunction[] functions)
    {
        var builder = new CoffObjectBuilder(Machine);
        foreach (var function in functions)
        {
            AddFunction(builder, function);
        }

        // Set once the initializers of a thread have run.
        builder.External("__tls_guard", builder.AddSection(
            ".tls$", new byte[1], CoffSectionFlags.InitializedData | CoffSectionFlags.Read | CoffSectionFlags.Write));

        // Compilers put the dynamic initializers of thread_local variables between these markers.
        builder.External("__xd_a", builder.ReadOnlyData(".CRT$XDA", new byte[8], 8));
        builder.External("__xd_z", builder.ReadOnlyData(".CRT$XDZ", new byte[8], 8));

        // Registers the first function as a TLS callback, so that it runs for every new thread.
        var callback = builder.ReadOnlyData(".CRT$XLC", new byte[8], 8);
        builder.Relocate(callback, 0, Addr64Relocation, functions[0].Name);
        return builder.Build();
    }

    private byte[] BuildStackProtection(PeFunction[] functions)
    {
        var builder = new CoffObjectBuilder(Machine);
        foreach (var function in functions)
        {
            AddFunction(builder, function);
        }

        var cookie = new byte[8];
        LittleEndian.U64(cookie, 0, DefaultSecurityCookie);
        builder.External("__security_cookie", builder.Data(".data", cookie, 8));

        // IMAGE_LOAD_CONFIG_DIRECTORY64: all that Ina uses it for is to tell the loader where the cookie is.
        var configuration = new byte[LoadConfigurationSize];
        LittleEndian.U32(configuration, 0, LoadConfigurationSize);
        var section = builder.ReadOnlyData(".rdata", configuration, 8);
        builder.External("_load_config_used", section);
        builder.Relocate(section, LoadConfigurationSecurityCookieOffset, Addr64Relocation, "__security_cookie");
        return builder.Build();
    }

    private byte[] BuildControlFlowGuard((string Pointer, PeFunction Function)[] stubs)
    {
        var builder = new CoffObjectBuilder(Machine);
        foreach (var (pointer, function) in stubs)
        {
            AddFunction(builder, function, isExternal: false);
            var slot = builder.ReadOnlyData(".00cfg", new byte[8], 8);
            builder.External(pointer, slot);
            builder.Relocate(slot, 0, Addr64Relocation, function.Name);
        }

        return builder.Build();
    }

    private byte[] BuildAbsolute(string name, uint value)
    {
        var builder = new CoffObjectBuilder(Machine);
        builder.Absolute(name, value);
        return builder.Build();
    }

    private byte[] BuildData(string name, byte[] content, bool writable)
    {
        var builder = new CoffObjectBuilder(Machine);
        builder.External(name, writable ? builder.Data(".data", content, 8) : builder.ReadOnlyData(".rdata", content, 1));
        return builder.Build();
    }

    protected static LinkerException Unsupported(PeTarget target, CoffSection section, in CoffRelocation relocation, PeSymbol symbol) =>
        new($"{section.DisplayName}: unsupported relocation {target.RelocationName(relocation.Type)} against '{symbol.Name}'");

    protected static LinkerException OutOfRange(PeTarget target, CoffSection section, in CoffRelocation relocation, PeSymbol symbol) =>
        new($"{section.DisplayName}+0x{relocation.Offset:x}: relocation {target.RelocationName(relocation.Type)} against '{symbol.Name}' is out of range");

    /// <summary>
    /// The address a relocation refers to, relative to the image base. An absolute symbol has no place in the
    /// image; its value is used as an address, and an unresolved weak symbol counts as zero.
    /// </summary>
    protected static long RelativeAddress(PeLinkContext context, PeSymbol symbol) => symbol.Kind switch
    {
        PeSymbolKind.Defined => symbol.Rva,
        PeSymbolKind.Absolute => (long)(symbol.Value - context.ImageBase),
        _ => 0,
    };
}
