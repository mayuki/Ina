#:project ../src/Ina.Linker/Ina.Linker.csproj
#:property TreatWarningsAsErrors=false

// Generates an import table for the Ina linker from the text-based stubs (.tbd) of a macOS SDK.
//
// usage: dotnet run eng/GenerateMacOSAbi.cs -- <output> <arm64|x64> <minimum-os> <sdk-version> <needed,needed,...>
//            <runtime-pack-native-dir>[,<dir>...] <all|ref>=<stub>...
//
// "all" lists every symbol a library exports, "ref" only those that the native libraries of the .NET runtime
// packs refer to. The stubs are given in the order in which a symbol exported by several of them is attributed.

using Ina.Linker;
using Ina.Linker.Abi;

if (args.Length < 7)
{
    Console.Error.WriteLine("usage: GenerateMacOSAbi <output> <arm64|x64> <minimum-os> <sdk-version> <needed,...> <native-dir,...> <all|ref>=<stub>...");
    return 1;
}

var architecture = args[1] == "arm64" ? TargetArchitecture.Arm64 : TargetArchitecture.X64;
var runtimeFiles = args[5].Split(',', StringSplitOptions.RemoveEmptyEntries)
    .SelectMany(static directory => Directory.EnumerateFiles(directory).Where(static file => file.EndsWith(".a") || file.EndsWith(".o")))
    .Order(StringComparer.Ordinal)
    .Select(LinkInput.FromFile);

var libraries = new List<MacOSLibraryStub>();
foreach (var argument in args[6..])
{
    var separator = argument.IndexOf('=');
    libraries.Add(new MacOSLibraryStub(File.ReadAllText(argument[(separator + 1)..]), Complete: argument[..separator] == "all"));
}

IReadOnlyList<string> missing;
using (var writer = new StreamWriter(args[0]))
{
    missing = MacOSAbiTableGenerator.Generate(writer, architecture, Version.Parse(args[2]), Version.Parse(args[3]), libraries, args[4].Split(','), runtimeFiles);
}

// Symbols that no library exports are either provided by the linker itself or point at a library missing from the list.
foreach (var name in missing)
{
    Console.Error.WriteLine($"not exported by any library: {name}");
}

return 0;
