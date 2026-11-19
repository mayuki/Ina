using System.Diagnostics;
using System.Runtime.InteropServices;
using Ina.Linker.Abi;
using Ina.Linker.MachO;

namespace Ina.Linker.Tests.Support;

/// <summary>Common plumbing for tests that link Mach-O objects and optionally run the result.</summary>
public abstract class MachOLinkerTestBase : IDisposable
{
    /// <summary>The translation layer that lets Apple silicon Macs run x86-64 programs.</summary>
    private const string RosettaRuntime = "/Library/Apple/usr/libexec/oah/libRosettaRuntime";

    protected const uint Ret = 0xd65f03c0;
    protected const uint StpFrame = 0xa9bf7bfd; // stp x29, x30, [sp, #-16]!
    protected const uint LdpFrame = 0xa8c17bfd; // ldp x29, x30, [sp], #16

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ina-tests", Guid.NewGuid().ToString("N"));
    private int _outputs;

    protected MachOLinkerTestBase()
    {
        Directory.CreateDirectory(_directory);
    }

    /// <summary>Whether executables for Apple silicon Macs can be run on this machine.</summary>
    protected static bool CanRunMacOSArm64 => OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    /// <summary>Whether executables for Intel Macs can be run on this machine: natively, or through Rosetta.</summary>
    protected static bool CanRunMacOSX64 =>
        OperatingSystem.IsMacOS() && (RuntimeInformation.ProcessArchitecture == Architecture.X64 || File.Exists(RosettaRuntime));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of temporary files.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Links the inputs into an executable for Apple silicon.</summary>
    internal string Link(params byte[][] inputs) => Link(TargetArchitecture.Arm64, static o => o, inputs);

    /// <summary>Links the inputs into an executable for Intel Macs.</summary>
    internal string LinkX64(params byte[][] inputs) => Link(TargetArchitecture.X64, static o => o, inputs);

    /// <summary>Links the inputs with default options adjusted by <paramref name="configure"/> and returns the output path.</summary>
    internal string Link(TargetArchitecture architecture, Func<LinkOptions, LinkOptions> configure, params byte[][] inputs)
    {
        var output = Path.Combine(_directory, $"out{_outputs++}");
        var options = new LinkOptions
        {
            OutputPath = output,
            Architecture = architecture,
            Imports = ImportLibrarySet.ForMacOS(architecture),
            Inputs = inputs.Select(static (data, i) => new LinkInput($"input{i}", data)).ToList(),
            EmitSymbolTable = true,
        };
        MachOLinker.Link(configure(options));
        return output;
    }

    internal static MachOImage Load(string path) => new(File.ReadAllBytes(path));

    /// <summary>Runs a linked arm64 executable and returns its exit code and output.</summary>
    protected static (int ExitCode, string Output) Run(string path)
    {
        Assert.SkipUnless(CanRunMacOSArm64, "Running the linked executable requires a Mac with Apple silicon.");
        return TestProcess.Run(new ProcessStartInfo(path));
    }

    /// <summary>Runs a linked x86-64 executable and returns its exit code and output.</summary>
    protected static (int ExitCode, string Output) RunX64(string path)
    {
        Assert.SkipUnless(CanRunMacOSX64, "Running the linked executable requires an Intel Mac, or a Mac with Rosetta.");
        return TestProcess.Run(new ProcessStartInfo(path));
    }

    /// <summary>An arm64 object with a single global function.</summary>
    internal static byte[] Function(string name, params uint[] instructions)
    {
        var builder = new MachObjectBuilder();
        builder.Global(name, builder.Text(Code.Arm64(instructions)));
        return builder.Build();
    }

    /// <summary>An arm64 object whose <c>main</c> calls <paramref name="callee"/> and returns its result.</summary>
    internal static byte[] MainCalling(string callee) => CallerOf("_main", callee);

    /// <summary>An arm64 object with a function that calls <paramref name="callee"/> and returns its result.</summary>
    internal static byte[] CallerOf(string name, string callee)
    {
        var builder = new MachObjectBuilder();
        var text = builder.Text(Code.Arm64(StpFrame, 0x94000000, LdpFrame, Ret)); // bl callee
        builder.Global(name, text);
        builder.Arm64(text, 4, MachRelocArm64.Branch26, callee);
        return builder.Build();
    }

    /// <summary>The address that the 32-bit RIP-relative displacement at <paramref name="address"/> refers to (x86-64).</summary>
    /// <param name="trailing">The number of instruction bytes that follow the displacement.</param>
    internal static ulong RipTarget(MachOImage image, ulong address, int trailing = 0) =>
        address + 4 + (ulong)trailing + (ulong)(long)(int)image.U32(address);

    /// <summary>The destination of the <c>b</c> or <c>bl</c> instruction at <paramref name="address"/>.</summary>
    internal static ulong BranchTarget(MachOImage image, ulong address)
    {
        var instruction = image.U32(address);
        Assert.Equal(0x14000000u, instruction & 0x7c000000);
        return address + (ulong)(((long)(instruction & 0x03ffffff) << 38) >> 36);
    }

    /// <summary>The page that the <c>adrp</c> instruction at <paramref name="address"/> computes.</summary>
    internal static ulong AdrpTarget(MachOImage image, ulong address)
    {
        var instruction = image.U32(address);
        Assert.Equal(0x90000000u, instruction & 0x9f000000);
        var pages = (((long)((instruction >> 5) & 0x7ffff) << 2) | ((instruction >> 29) & 3)) << 43 >> 43;
        return (address & ~0xfffUL) + (ulong)(pages << 12);
    }

    /// <summary>The 12-bit immediate of the <c>add</c>, load or store instruction at <paramref name="address"/>.</summary>
    internal static ulong Imm12(MachOImage image, ulong address) => (image.U32(address) >> 10) & 0xfff;
}
