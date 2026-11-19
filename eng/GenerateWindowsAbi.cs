#:project ../src/Ina.Linker/Ina.Linker.csproj
#:property TreatWarningsAsErrors=false

// Regenerates the import table for Windows targets (src/Ina.Linker/Abi/Tables/windows.abi) from the export tables
// of the system DLLs of the machine it runs on. Only the names of the exports are extracted.
//
// usage (on Windows): dotnet run eng/GenerateWindowsAbi.cs -- [output]
//
// Only needed when the runtime starts to use an API that the table does not know yet.

using Ina.Linker.Abi;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("The table is generated from the system DLLs, which only exist on Windows.");
    return 1;
}

var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
var output = args.Length > 0 ? args[0] : Path.Combine("src", "Ina.Linker", "Abi", "Tables", "windows.abi");

// A name that several DLLs export is attributed to the first one listed here. The Win32 libraries come in the
// order of the official NativeAOT link line, followed by the C runtime and ntdll.
string[] win32 =
[
    "advapi32.dll", "bcrypt.dll", "crypt32.dll", "iphlpapi.dll", "kernel32.dll", "mswsock.dll", "ncrypt.dll", "normaliz.dll", "ole32.dll",
    "oleaut32.dll", "secur32.dll", "user32.dll", "version.dll", "ws2_32.dll",
];

// The Universal C Runtime is addressed through its API sets, like the official binaries do. The "downlevel"
// directory has a stub DLL per API set whose export table lists the functions of the set.
string[] ucrt =
[
    "conio", "convert", "environment", "filesystem", "heap", "locale", "math", "multibyte", "process", "runtime", "stdio", "string", "time",
    "utility", "private",
];

// kernel32.dll forwards a few support functions of compilers (__C_specific_handler, __chkstk) to ntdll.dll. They
// are not part of the Windows API and are not exported for every architecture; the C runtime provides them.
var libraries = new List<(ReadOnlyMemory<byte>, Func<string, bool>?)>();
foreach (var name in win32)
{
    Func<string, bool>? filter = name == "kernel32.dll" ? static export => !export.StartsWith("__", StringComparison.Ordinal) : null;
    libraries.Add((File.ReadAllBytes(Path.Combine(system, name)), filter));
}

// Synchronization.lib of the Windows SDK: the futex-like wait functions, which kernel32.dll does not export.
libraries.Add((
    File.ReadAllBytes(Path.Combine(system, "downlevel", "api-ms-win-core-synch-l1-2-0.dll")),
    static name => name is "WaitOnAddress" or "WakeByAddressAll" or "WakeByAddressSingle"));

foreach (var name in ucrt)
{
    libraries.Add((File.ReadAllBytes(Path.Combine(system, "downlevel", $"api-ms-win-crt-{name}-l1-1-0.dll")), null));
}

// ntdll.dll also exports a private copy of many C functions (memcpy, atexit, sprintf, ...) that are not meant for
// applications. Only its native API takes part: the prefixes of the system services and the run-time library.
string[] nativeApi = ["Nt", "Zw", "Rtl", "Ldr", "Etw", "Tp", "Dbg"];
libraries.Add((
    File.ReadAllBytes(Path.Combine(system, "ntdll.dll")),
    name => nativeApi.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length && char.IsAsciiLetterUpper(name[prefix.Length]))));

using var writer = new StreamWriter(output);
WindowsAbiTableGenerator.Generate(writer, $"windows {Environment.OSVersion.Version}", libraries);
return 0;
