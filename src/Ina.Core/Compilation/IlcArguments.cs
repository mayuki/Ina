namespace Ina.Compilation;

/// <summary>What the generated code is tuned for.</summary>
public enum OptimizationMode
{
    /// <summary>No optimization; the fastest compilation.</summary>
    None,

    /// <summary>A balance of speed and size; the default of NativeAOT.</summary>
    Blended,

    Size,

    Speed,
}

/// <summary>The inputs of one ILCompiler invocation.</summary>
public sealed record IlcOptions
{
    public required string InputAssembly { get; init; }

    public required string OutputObject { get; init; }

    public required TargetPlatform Target { get; init; }

    /// <summary>Assemblies that may be referenced: framework and application dependencies.</summary>
    public IReadOnlyList<string> References { get; init; } = [];

    public OptimizationMode Optimization { get; init; } = OptimizationMode.Blended;

    /// <summary>Runtime configuration of the application (<c>configProperties</c> of runtimeconfig.json).</summary>
    public IReadOnlyDictionary<string, string> RuntimeKnobs { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Files that list functions of system libraries which the compiler may call directly, see
    /// <see cref="Packs.RuntimePacks.DirectPInvokeLists"/>.
    /// </summary>
    public IReadOnlyList<string> DirectPInvokeLists { get; init; } = [];

    /// <summary>Emit DWARF debug information into the object file.</summary>
    public bool DebugInfo { get; init; }

    /// <summary>
    /// Version of the compiler, which is the version of the runtime packs. The command line of ILCompiler is not
    /// stable across major versions, so a few arguments depend on it.
    /// </summary>
    public Version RuntimeVersion { get; init; } = new(11, 0);
}

/// <summary>
/// Builds the ILCompiler command line. It mirrors what the NativeAOT MSBuild targets
/// (Microsoft.NETCore.Native.targets) pass for a published console application.
/// </summary>
public static class IlcArguments
{
    /// <summary>Feature switches that <c>PublishAot</c> sets by default; they let the compiler drop unused code paths.</summary>
    private static readonly Dictionary<string, string> DefaultKnobs = new(StringComparer.Ordinal)
    {
        ["Microsoft.Extensions.DependencyInjection.VerifyOpenGenericServiceTrimmability"] = "true",
        ["System.ComponentModel.DefaultValueAttribute.IsSupported"] = "false",
        ["System.ComponentModel.Design.IDesignerHost.IsSupported"] = "false",
        ["System.ComponentModel.TypeConverter.EnableUnsafeBinaryFormatterInDesigntimeLicenseContextSerialization"] = "false",
        ["System.ComponentModel.TypeDescriptor.IsComObjectDescriptorSupported"] = "false",
        ["System.Data.DataSet.XmlSerializationIsSupported"] = "false",
        ["System.Diagnostics.Tracing.EventSource.IsSupported"] = "false",
        ["System.Linq.Enumerable.IsSizeOptimized"] = "true",
        ["System.Net.Security.UseManagedNtlm"] = "false",
        ["System.Net.SocketsHttpHandler.Http3Support"] = "false",
        ["System.Reflection.Metadata.MetadataUpdater.IsSupported"] = "false",
        ["System.Resources.ResourceManager.AllowCustomResourceTypes"] = "false",
        ["System.Resources.UseSystemResourceKeys"] = "false",
        ["System.Runtime.InteropServices.BuiltInComInterop.IsSupported"] = "false",
        ["System.Runtime.InteropServices.EnableConsumingManagedCodeFromNativeHosting"] = "false",
        ["System.Runtime.InteropServices.EnableCppCLIHostActivation"] = "false",
        ["System.Runtime.InteropServices.Marshalling.EnableGeneratedComInterfaceComImportInterop"] = "false",
        ["System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization"] = "false",
        ["System.StartupHookProvider.IsSupported"] = "false",
        ["System.Text.Encoding.EnableUnsafeUTF7Encoding"] = "false",
        ["System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault"] = "false",
        ["System.Threading.Thread.EnableAutoreleasePool"] = "false",
        ["System.Linq.Expressions.CanEmitObjectArrayDelegate"] = "false",
    };

    /// <summary>Settings that ahead-of-time compilation dictates; the application cannot override them.</summary>
    private static readonly Dictionary<string, string> MandatoryKnobs = new(StringComparer.Ordinal)
    {
        ["System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported"] = "false",
    };

    private static readonly string[] InitAssemblies =
    [
        "System.Private.CoreLib", "System.Private.StackTraceMetadata", "System.Private.TypeLoader", "System.Private.Reflection.Execution",
    ];

    /// <summary>
    /// Native libraries of the framework that are linked statically on Linux and macOS, so P/Invokes into them
    /// become direct calls. The cryptography library of the target platform is added to them.
    /// </summary>
    private static readonly string[] DirectPInvokeLibraries =
    [
        "System.Native", "System.Globalization.Native", "System.IO.Compression.Native", "System.Net.Security.Native",
    ];

    /// <summary>
    /// The same for Windows, where most of the platform abstraction is implemented in managed code on top of the
    /// system DLLs.
    /// </summary>
    private static readonly string[] WindowsDirectPInvokeLibraries = ["System.Globalization.Native", "System.IO.Compression.Native"];

    /// <summary>
    /// The symbol through which diagnostic tools find the runtime; it stays visible in the executable. .NET 11
    /// replaced the debug header of earlier versions with the data contract descriptor.
    /// </summary>
    public static string DiagnosticsSymbol(Version runtimeVersion) =>
        runtimeVersion.Major >= 11 ? "DotNetRuntimeContractDescriptor" : "DotNetRuntimeDebugHeader";

    public static IReadOnlyList<string> Build(IlcOptions options)
    {
        var arguments = new List<string>
        {
            options.InputAssembly,
            $"-o:{options.OutputObject}",
        };
        arguments.AddRange(options.References.Select(static reference => $"-r:{reference}"));
        arguments.Add($"--targetos:{options.Target.OperatingSystemName}");
        arguments.Add($"--targetarch:{options.Target.ArchitectureName}");

        var isWindows = options.Target.OperatingSystem == TargetOperatingSystem.Windows;

        // Stores pointers in a compact relative form and expands them at startup, which keeps the image small and
        // reduces the number of load-time relocations. Windows relocates images cheaply, so there the official
        // build only trades startup time for size on request.
        if (!isWindows || options.Optimization == OptimizationMode.Size)
        {
            arguments.Add("--dehydrate");
        }

        if (options.Optimization != OptimizationMode.None)
        {
            arguments.Add("-O");
        }

        if (options.DebugInfo)
        {
            arguments.Add("-g");
        }

        // On Windows the compiler has to know that the exported symbol is a variable, not a function.
        arguments.Add($"--export-dynamic-symbol:{DiagnosticsSymbol(options.RuntimeVersion)}{(isWindows ? ",DATA" : string.Empty)}");
        arguments.AddRange(InitAssemblies.Select(static assembly => $"--initassembly:{assembly}"));
        IEnumerable<string> directPInvokeLibraries = isWindows
            ? WindowsDirectPInvokeLibraries
            : DirectPInvokeLibraries.Append(options.Target.CryptographyLibrary);
        arguments.AddRange(directPInvokeLibraries.Select(static library => $"--directpinvoke:{library}"));
        arguments.AddRange(options.DirectPInvokeLists.Select(static list => $"--directpinvokelist:{list}"));

        var knobs = MergeKnobs(options.RuntimeKnobs);
        foreach (var (name, value) in knobs)
        {
            // Boolean settings double as feature switches that the compiler evaluates at build time.
            if (value is "true" or "false")
            {
                arguments.Add($"--feature:{name}={value}");
            }
        }

        arguments.AddRange(knobs.Select(static knob => $"--runtimeknob:{knob.Key}={knob.Value}"));
        arguments.Add($"--runtimeknob:RUNTIME_IDENTIFIER={options.Target.RuntimeIdentifier}");

        // Stack traces show method names. Since .NET 11 the switch takes the level of detail.
        arguments.Add(options.RuntimeVersion.Major >= 11 ? "--stacktracedata:frames" : "--stacktracedata");
        arguments.Add("--scanreflection");
        if (isWindows)
        {
            // Copies the Win32 resources of the application (version information, icon, manifest) into the object.
            arguments.Add($"--win32resourcemodule:{Path.GetFileNameWithoutExtension(options.InputAssembly)}");
        }

        arguments.Add($"--methodbodyfolding:{(options.Optimization == OptimizationMode.None ? "none" : "generic")}");
        switch (options.Optimization)
        {
            case OptimizationMode.Size:
                arguments.Add("--Os");
                break;
            case OptimizationMode.Speed:
                arguments.Add("--Ot");
                break;
        }

        // Report trimming problems of libraries once per assembly, but in full for the application itself.
        arguments.Add("--singlewarn");
        arguments.Add($"--nosinglewarnassembly:{Path.GetFileNameWithoutExtension(options.InputAssembly)}");
        arguments.Add("--resilient");
        arguments.Add("--generateunmanagedentrypoints:System.Private.CoreLib,HIDDEN");
        arguments.Add("--feature:System.Diagnostics.Debugger.IsSupported=false");
        return arguments;
    }

    /// <summary>Formats arguments for a response file: one per line, quoted when they contain whitespace.</summary>
    public static IEnumerable<string> ToResponseFileLines(IEnumerable<string> arguments) =>
        arguments.Select(static argument => argument.Any(char.IsWhiteSpace) ? $"\"{argument}\"" : argument);

    private static SortedDictionary<string, string> MergeKnobs(IReadOnlyDictionary<string, string> applicationKnobs)
    {
        var knobs = new SortedDictionary<string, string>(DefaultKnobs, StringComparer.Ordinal);
        foreach (var (name, value) in applicationKnobs)
        {
            knobs[name] = value;
        }

        foreach (var (name, value) in MandatoryKnobs)
        {
            knobs[name] = value;
        }

        return knobs;
    }
}
