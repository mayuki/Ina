#:project ../src/Ina.Linker/Ina.Linker.csproj
#:property TreatWarningsAsErrors=false

// Generates an import table for the Ina linker from the shared libraries of a target system.
//
// usage: dotnet run eng/GenerateAbi.cs -- <output> <description> <interpreter> <needed,needed,...> <soname>=<path>...
//
// The libraries are given in the order in which a symbol exported by several of them is attributed.

using Ina.Linker.Abi;

if (args.Length < 5)
{
    Console.Error.WriteLine("usage: GenerateAbi <output> <description> <interpreter> <needed,...> <soname>=<path>...");
    return 1;
}

var libraries = new List<(string, ReadOnlyMemory<byte>)>();
foreach (var argument in args[4..])
{
    var separator = argument.IndexOf('=');
    libraries.Add((argument[..separator], File.ReadAllBytes(argument[(separator + 1)..])));
}

using var writer = new StreamWriter(args[0]);
AbiTableGenerator.Generate(writer, args[1], args[2], libraries, args[3].Split(','));
return 0;
