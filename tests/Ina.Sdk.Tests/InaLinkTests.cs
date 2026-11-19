using System.Diagnostics;
using System.Runtime.InteropServices;
using Ina.Linker;
using Ina.Linker.Elf;
using Ina.Linker.MachO;
using Ina.Linker.Tests.Support;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Ina.Sdk.Tests;

/// <summary>
/// The task that takes the place of the native toolchain in the NativeAOT targets: it receives the object file of
/// the application and the runtime libraries, and writes the executable.
/// </summary>
public sealed class InaLinkTests : IDisposable
{
    private const uint Arm64Ret = 0xd65f03c0;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ina-tests", Guid.NewGuid().ToString("N"));
    private readonly RecordingBuildEngine _engine = new();

    public InaLinkTests() => Directory.CreateDirectory(_directory);

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
    }

    [Fact]
    public void LinksTheInputsIntoAnExecutable()
    {
        var task = CreateTask("linux-x64", Code.MainCalling("helper"), Code.Function("helper", Code.Return(42)));

        Assert.True(task.Execute(), Describe(_engine.Errors));
        Assert.Empty(_engine.Errors);
        Assert.Empty(_engine.Warnings);
        Assert.True(File.Exists(task.OutputPath));

        // One line tells the user what was produced; it is the only output of a successful link at normal verbosity.
        var summary = Assert.Single(_engine.Messages, static m => m.Importance == MessageImportance.High);
        Assert.Contains("linux-x64", summary.Message);
        Assert.Contains(Path.GetFileName(task.OutputPath), summary.Message);
        Assert.Contains("glibc 2.2.5+", summary.Message);

        Assert.SkipUnless(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64, "Running the executable requires an x86-64 Linux host.");
        using var process = Process.Start(new ProcessStartInfo(task.OutputPath) { UseShellExecute = false })!;
        process.WaitForExit();
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public void RuntimeIdentifierSelectsTheArchitecture()
    {
        var builder = new ElfObjectBuilder(ElfMachine.AArch64);
        builder.Global("main", builder.Text(".text", Code.Arm64(0x52800540, Arm64Ret))); // mov w0, #42 ; ret
        var task = CreateTask("linux-arm64", builder.Build());

        Assert.True(task.Execute(), Describe(_engine.Errors));

        var image = new ElfImage(File.ReadAllBytes(task.OutputPath));
        Assert.Equal((ushort)ElfMachine.AArch64, image.Machine);
        Assert.Equal("/lib/ld-linux-aarch64.so.1", image.Interpreter);
    }

    [Fact]
    public void ExecutableIsPositionIndependentUnlessDisabled()
    {
        var relocatable = CreateTask("linux-x64", Code.Function("main", Code.Return(0)));
        var fixedAddress = CreateTask("linux-x64", Code.Function("main", Code.Return(0)));
        fixedAddress.PositionIndependent = false;

        Assert.True(relocatable.Execute(), Describe(_engine.Errors));
        Assert.True(fixedAddress.Execute(), Describe(_engine.Errors));

        Assert.Equal(ElfType.Dyn, new ElfImage(File.ReadAllBytes(relocatable.OutputPath)).Type);
        Assert.Equal(ElfType.Exec, new ElfImage(File.ReadAllBytes(fixedAddress.OutputPath)).Type);
    }

    [Fact]
    public void SymbolTableIsWrittenOnlyOnRequest()
    {
        var stripped = CreateTask("linux-x64", Code.Function("main", Code.Return(0)));
        var withSymbols = CreateTask("linux-x64", Code.Function("main", Code.Return(0)));
        withSymbols.EmitSymbolTable = true;

        Assert.True(stripped.Execute(), Describe(_engine.Errors));
        Assert.True(withSymbols.Execute(), Describe(_engine.Errors));

        Assert.Null(new ElfImage(File.ReadAllBytes(stripped.OutputPath)).FindSection(".symtab"));
        Assert.Contains("main", new ElfImage(File.ReadAllBytes(withSymbols.OutputPath)).Symbols.Keys);
    }

    [Fact]
    public void SymbolsOfTheExportsFileStayVisibleInTheExecutable()
    {
        // ILCompiler lists the symbols that tools look up at run time in a linker version script.
        var builder = new ElfObjectBuilder();
        builder.Global("main", builder.Text(".text", Code.Return(0)));
        builder.Global("DotNetRuntimeDebugHeader", builder.Data(".data", new byte[8]), size: 8, type: SymbolType.Object);
        var task = CreateTask("linux-x64", builder.Build());
        task.ExportsFile = Path.Combine(_directory, "app.exports");
        File.WriteAllText(task.ExportsFile, "V1.0 {\n    global:\n        DotNetRuntimeDebugHeader;\n    local: *;\n};\n");

        Assert.True(task.Execute(), Describe(_engine.Errors));

        Assert.Contains("DotNetRuntimeDebugHeader", new ElfImage(File.ReadAllBytes(task.OutputPath)).DynamicSymbols);
    }

    [Fact]
    public void MacOSRuntimeIdentifierProducesAMachOExecutable()
    {
        var task = CreateTask("osx-arm64", MachFunction("_main", 0x52800540, Arm64Ret)); // mov w0, #42 ; ret

        Assert.True(task.Execute(), Describe(_engine.Errors));
        Assert.Empty(_engine.Errors);
        Assert.Empty(_engine.Warnings);

        // What the executable requires of the system is a version of macOS, not of a C library.
        var summary = Assert.Single(_engine.Messages, static m => m.Importance == MessageImportance.High);
        Assert.Contains("osx-arm64", summary.Message);
        Assert.Contains("macOS 12.0+", summary.Message);
        Assert.DoesNotContain("glibc", summary.Message);

        var image = new MachOImage(File.ReadAllBytes(task.OutputPath));
        Assert.Equal(MachCpu.Arm64, image.CpuType);
        Assert.True(image.Signature!.HashesMatch);

        Assert.SkipUnless(OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64, "Running the executable requires a Mac with Apple silicon.");
        using var process = Process.Start(new ProcessStartInfo(task.OutputPath) { UseShellExecute = false })!;
        process.WaitForExit();
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public void MacOSRuntimeIdentifierSelectsTheArchitecture()
    {
        var builder = new MachObjectBuilder(TargetArchitecture.X64);
        builder.Global("_main", builder.Text(0xb8, 0x2a, 0x00, 0x00, 0x00, 0xc3)); // mov eax, 42 ; ret
        var task = CreateTask("osx-x64", builder.Build());

        Assert.True(task.Execute(), Describe(_engine.Errors));

        Assert.Equal(MachCpu.X64, new MachOImage(File.ReadAllBytes(task.OutputPath)).CpuType);
    }

    [Fact]
    public void SymbolsOfTheExportsFileStayVisibleInAMacOSExecutable()
    {
        // For Apple's linker ILCompiler writes a plain list of symbols, spelled the way object files spell them.
        var builder = new MachObjectBuilder();
        builder.Global("_main", builder.Text(Code.Arm64(0x52800000, Arm64Ret))); // mov w0, #0 ; ret
        builder.Global("_DotNetRuntimeDebugHeader", builder.Data(new byte[8]));
        var hidden = CreateTask("osx-arm64", builder.Build());
        var exported = CreateTask("osx-arm64", builder.Build());
        exported.ExportsFile = Path.Combine(_directory, "app.exports");
        File.WriteAllText(exported.ExportsFile, "_DotNetRuntimeDebugHeader\n");

        Assert.True(hidden.Execute(), Describe(_engine.Errors));
        Assert.True(exported.Execute(), Describe(_engine.Errors));

        Assert.DoesNotContain("_DotNetRuntimeDebugHeader", new MachOImage(File.ReadAllBytes(hidden.OutputPath)).Exports.Keys);
        Assert.Contains("_DotNetRuntimeDebugHeader", new MachOImage(File.ReadAllBytes(exported.OutputPath)).Exports.Keys);
    }

    [Fact]
    public void FixedAddressExecutableIsAnErrorForMacOS()
    {
        var task = CreateTask("osx-arm64", MachFunction("_main", 0x52800000, Arm64Ret));
        task.PositionIndependent = false;

        Assert.False(task.Execute());

        var error = Assert.Single(_engine.Errors);
        Assert.Equal("INA2001", error.Code);
        Assert.Contains("position independent", error.Message);
        Assert.False(File.Exists(task.OutputPath));
    }

    [Theory]
    [InlineData("win-x64")]
    [InlineData("linux-musl-x64")]
    [InlineData("ios-arm64")]
    [InlineData("")]
    public void UnsupportedRuntimeIdentifierIsAnError(string runtimeIdentifier)
    {
        var task = CreateTask(runtimeIdentifier, Code.Function("main", Code.Return(0)));

        Assert.False(task.Execute());

        var error = Assert.Single(_engine.Errors);
        Assert.Equal("INA1002", error.Code);
        Assert.Contains("linux-x64, linux-arm64, osx-x64, osx-arm64", error.Message);
        Assert.False(File.Exists(task.OutputPath));
    }

    [Fact]
    public void MissingInputIsAnError()
    {
        var task = CreateTask("linux-x64", Code.Function("main", Code.Return(0)));
        var missing = Path.Combine(_directory, "libMissing.a");
        task.Inputs = [.. task.Inputs, new TaskItem(missing)];

        Assert.False(task.Execute());

        var error = Assert.Single(_engine.Errors);
        Assert.Equal("INA2002", error.Code);
        Assert.Contains(missing, error.Message);
    }

    [Fact]
    public void LinkFailureIsReportedWithTheMessageOfTheLinker()
    {
        var task = CreateTask("linux-x64", Code.MainCalling("a_function_that_nobody_defines"));

        Assert.False(task.Execute());

        var error = Assert.Single(_engine.Errors);
        Assert.Equal("INA2001", error.Code);
        Assert.Contains("a_function_that_nobody_defines", error.Message);
    }

    private InaLink CreateTask(string runtimeIdentifier, params byte[][] objects)
    {
        var index = Directory.GetFiles(_directory).Length;
        var inputs = objects.Select((data, i) =>
        {
            var path = Path.Combine(_directory, $"input{index}-{i}.o");
            File.WriteAllBytes(path, data);
            return (ITaskItem)new TaskItem(path);
        }).ToArray();

        return new InaLink
        {
            BuildEngine = _engine,
            Inputs = inputs,
            OutputPath = Path.Combine(_directory, $"app{index}"),
            RuntimeIdentifier = runtimeIdentifier,
        };
    }

    /// <summary>An arm64 Mach-O object with a single global function.</summary>
    private static byte[] MachFunction(string name, params uint[] instructions)
    {
        var builder = new MachObjectBuilder();
        builder.Global(name, builder.Text(Code.Arm64(instructions)));
        return builder.Build();
    }

    private static string Describe(IEnumerable<BuildErrorEventArgs> errors) => string.Join(Environment.NewLine, errors.Select(static e => $"{e.Code}: {e.Message}"));
}
