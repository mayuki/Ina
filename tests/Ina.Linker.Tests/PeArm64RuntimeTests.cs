using Ina.Linker.Pe;
using Ina.Linker.Tests.Support;

namespace Ina.Linker.Tests;

/// <summary>
/// The code that the linker generates for ARM64 Windows programs, executed in an emulator. The x64 versions of
/// the same functions are run for real by <see cref="PeExecutableTests"/>; no ARM64 Windows machine is needed here.
/// </summary>
public class PeArm64RuntimeTests : PeLinkerTestBase
{
    private const uint Ret = 0xd65f03c0;

    [Fact]
    public void StartupCodePreparesTheRuntimeRunsInitializersAndCallsMainWithTheCommandLine()
    {
        var builder = new CoffObjectBuilder(CoffMachine.Arm64);
        var seen = builder.Bss(".bss", 16);
        var text = builder.Text(".text$mn", Code.Arm64(
            0x90000008,  // wmain: adrp x8, seen
            0x91000108,  //        add  x8, x8, :lo12:seen
            0xf9000101,  //        str  x1, [x8]              ; remember argv
            0x1100a000,  //        add  w0, w0, #40           ; return argc + 40
            Ret,
            0x90000008,  // ctor:  adrp x8, seen
            0x91000108,  //        add  x8, x8, :lo12:seen
            0x52800029,  //        mov  w9, #1
            0xb9000909,  //        str  w9, [x8, #8]          ; note that the constructor ran
            Ret));
        builder.External("wmain", text);
        builder.External("seen", seen);
        foreach (var offset in new uint[] { 0, 20 })
        {
            builder.Relocate(text, offset, RelArm64.PageBaseRel21, seen);
            builder.Relocate(text, offset + 4, RelArm64.PageOffset12A, seen);
        }

        // A static constructor, registered the way compilers do it; its address is 20 bytes into the code.
        var initializers = builder.ReadOnlyData(".CRT$XCU", [20, 0, 0, 0, 0, 0, 0, 0], 8);
        builder.Relocate(initializers, 0, RelArm64.Addr64, text);

        var image = Load(Link(static o => o with { Architecture = TargetArchitecture.Arm64, ExportedSymbols = ["seen"] }, builder.Build()));
        var emulator = CreateEmulator(image);

        // What the C runtime DLL would do, reduced to what the startup code relies on.
        const ulong Arguments = 0x5000_0000;
        var calls = new List<string>();
        emulator.Write32(Arguments, 2);                 // argc
        emulator.Write64(Arguments + 8, 0x6000_0000);   // argv
        emulator.Import = (name, machine) =>
        {
            calls.Add(name is "_set_app_type" or "_configure_wide_argv" or "exit" ? $"{name}({machine.X[0]})" : name);
            switch (name)
            {
                case "_initterm":
                    for (var entry = machine.X[0]; entry < machine.X[1]; entry += 8)
                    {
                        if (machine.Read64(entry) is not 0 and var function)
                        {
                            machine.Call(function);
                        }
                    }

                    return 0;
                case "__p___argc":
                    return Arguments;
                case "__p___wargv":
                    return Arguments + 8;
                case "exit":
                    throw new ProgramExitException((int)machine.X[0]);
                default:
                    return 0;
            }
        };

        var exit = Assert.Throws<ProgramExitException>(() => emulator.Call(emulator.AddressOf(image.EntryPoint)));

        Assert.Equal(
            ["_set_app_type(1)", "_configure_wide_argv(1)", "_initterm_e", "_initterm", "__p___argc", "__p___wargv", "exit(42)"],
            calls);
        Assert.Equal(42, exit.Code);
        var state = emulator.AddressOf(image.Exports["seen"]);
        Assert.Equal(0x6000_0000ul, emulator.Read64(state));
        Assert.Equal(1u, emulator.Read32(state + 8));
    }

    [Fact]
    public void StartupCodeStopsWithAnErrorWhenAnInitializerFails()
    {
        var image = Load(LinkArm64(EmptyMain()));
        var emulator = CreateEmulator(image);
        var reachedMain = false;
        emulator.Import = (name, _) =>
        {
            reachedMain |= name == "__p___argc";
            return name == "_initterm_e" ? 1u : 0u;
        };

        // The entry point returns to the system instead of calling main; 255 is what the C runtime uses as well.
        Assert.Equal(255ul, emulator.Call(emulator.AddressOf(image.EntryPoint)));
        Assert.False(reachedMain);
        Assert.Equal(0x7fff_0000ul, emulator.Sp);
    }

    [Theory]
    [InlineData("_InterlockedCompareExchange", 4)]
    [InlineData("_InterlockedCompareExchange64", 8)]
    [InlineData("_InterlockedCompareExchangePointer", 8)]
    public void CompareExchangeStoresOnlyWhenTheValueIsTheExpectedOne(string function, int size)
    {
        var (emulator, address) = EmulatorFor(function);
        var (five, nine) = size == 8 ? (0x5_0000_0005ul, 0x9_0000_0009ul) : (5ul, 9ul);
        const ulong Cell = 0x5000_0000;
        emulator.Write64(Cell, size == 8 ? five : 0xffff_ffff_0000_0005);

        // Not the expected value: nothing is stored, and the current value comes back.
        Assert.Equal(five, emulator.Call(address, Cell, nine, 7));
        Assert.Equal(five, emulator.Read64(Cell) & Mask(size));

        // The expected value: the new value is stored, and the previous one comes back.
        Assert.Equal(five, emulator.Call(address, Cell, nine, five));
        Assert.Equal(nine, emulator.Read64(Cell) & Mask(size));

        // A 32-bit operation leaves the neighboring memory alone.
        Assert.Equal(size == 8 ? nine : 0xffff_ffff_0000_0009, emulator.Read64(Cell));
    }

    [Theory]
    [InlineData("_InterlockedExchange", 4, 100ul, 7ul, 100ul, 7ul)]
    [InlineData("_InterlockedExchange64", 8, 0x1_0000_0064ul, 0x2_0000_0007ul, 0x1_0000_0064ul, 0x2_0000_0007ul)]
    [InlineData("_InterlockedExchangePointer", 8, 0x1_0000_0064ul, 0x2_0000_0007ul, 0x1_0000_0064ul, 0x2_0000_0007ul)]
    [InlineData("_InterlockedExchangeAdd", 4, 100ul, 7ul, 100ul, 107ul)]
    [InlineData("_InterlockedExchangeAdd64", 8, 0xffff_fffful, 1ul, 0xffff_fffful, 0x1_0000_0000ul)]
    [InlineData("_InterlockedIncrement", 4, 100ul, 0ul, 101ul, 101ul)]
    [InlineData("_InterlockedIncrement64", 8, 0xffff_fffful, 0ul, 0x1_0000_0000ul, 0x1_0000_0000ul)]
    [InlineData("_InterlockedDecrement", 4, 100ul, 0ul, 99ul, 99ul)]
    [InlineData("_InterlockedDecrement64", 8, 0x1_0000_0000ul, 0ul, 0xffff_fffful, 0xffff_fffful)]
    [InlineData("_InterlockedAnd", 4, 0b1100ul, 0b1010ul, 0b1100ul, 0b1000ul)]
    [InlineData("_InterlockedOr", 4, 0b1100ul, 0b1010ul, 0b1100ul, 0b1110ul)]
    [InlineData("_InterlockedXor", 4, 0b1100ul, 0b1010ul, 0b1100ul, 0b0110ul)]
    [InlineData("_InterlockedAnd64", 8, 0xf_0000_000cul, 0x3_0000_000aul, 0xf_0000_000cul, 0x3_0000_0008ul)]
    [InlineData("_InterlockedOr64", 8, 0x4_0000_000cul, 0x3_0000_000aul, 0x4_0000_000cul, 0x7_0000_000eul)]
    [InlineData("_InterlockedXor64", 8, 0x6_0000_000cul, 0x3_0000_000aul, 0x6_0000_000cul, 0x5_0000_0006ul)]
    public void InterlockedOperationsUpdateMemoryAndReturnWhatWindowsDocuments(
        string function, int size, ulong initial, ulong operand, ulong expectedResult, ulong expectedMemory)
    {
        var (emulator, address) = EmulatorFor(function);
        const ulong Cell = 0x5000_0000;
        emulator.Write64(Cell, size == 8 ? initial : 0xffff_ffff_0000_0000 | initial);

        var result = emulator.Call(address, Cell, operand);

        // Exchange and the bitwise operations return the previous value; increment and decrement the new one.
        Assert.Equal(expectedResult, result);
        Assert.Equal(size == 8 ? expectedMemory : 0xffff_ffff_0000_0000 | expectedMemory, emulator.Read64(Cell));
    }

    [Fact]
    public void StackProbeTouchesEveryPageOfTheNewFrameFromTheTopDown()
    {
        var (emulator, address) = EmulatorFor("__chkstk");
        var stack = emulator.Sp;

        // The size comes in x15, in units of 16 bytes: two and a half pages here.
        emulator.X[15] = 0x2800 / 16;
        emulator.TouchedPages.Clear();
        emulator.Call(address);

        Assert.Equal([stack - 0x1000, stack - 0x2000, stack - 0x3000], emulator.TouchedPages.Where(page => page < stack));

        // The caller subtracts x15 from the stack pointer itself afterwards.
        Assert.Equal(0x2800ul / 16, emulator.X[15]);
        Assert.Equal(stack, emulator.Sp);
    }

    [Fact]
    public void StackProtectionPushesTheCookieAndChecksItOnTheWayOut()
    {
        var image = Load(Link(
            static o => o with { Architecture = TargetArchitecture.Arm64, ExportedSymbols = ["__security_push_cookie", "__security_pop_cookie", "__security_cookie"] },
            EmptyMain()));
        var emulator = CreateEmulator(image);
        var push = emulator.AddressOf(image.Exports["__security_push_cookie"]);
        var pop = emulator.AddressOf(image.Exports["__security_pop_cookie"]);
        var cookie = emulator.Read64(emulator.AddressOf(image.Exports["__security_cookie"]));
        var stack = emulator.Sp;
        ulong[] arguments = [11, 22, 33, 44, 55, 66, 77, 88];

        // In a prolog: 16 bytes are reserved, the cookie is in the upper half, and the arguments of the protected
        // function are still intact.
        emulator.Call(push, arguments);
        Assert.Equal(stack - 16, emulator.Sp);
        Assert.Equal(cookie, emulator.Read64(emulator.Sp + 8));
        Assert.Equal(arguments, emulator.X[..8]);

        // In an epilog: the stack is released, and the return value in x0 survives.
        emulator.Write64(emulator.Sp, 0x1234); // the lower half belongs to the function
        Assert.Equal(11ul, emulator.Call(pop, 11));
        Assert.Equal(stack, emulator.Sp);

        // A cookie that was overwritten stops the program: __fastfail(FAST_FAIL_STACK_COOKIE_CHECK_FAILURE).
        emulator.Call(push);
        emulator.Write64(emulator.Sp + 8, cookie ^ 1);
        var trap = Assert.Throws<Arm64TrapException>(() => emulator.Call(pop));
        Assert.Equal(0xf003u, trap.Code);
        Assert.Equal(2ul, emulator.X[0]);
    }

    [Fact]
    public void DynamicInitializersOfThreadLocalsRunOncePerThread()
    {
        var builder = new CoffObjectBuilder(CoffMachine.Arm64);
        var counter = builder.Bss(".bss", 4, 4);
        var text = builder.Text(".text$mn", Code.Arm64(
            Ret,         // wmain
            0x90000008,  // init: adrp x8, counter
            0xb9400109,  //       ldr  w9, [x8, :lo12:counter]
            0x11000529,  //       add  w9, w9, #1
            0xb9000109,  //       str  w9, [x8, :lo12:counter]
            Ret));
        builder.External("wmain", text);
        builder.External("counter", counter);
        builder.Relocate(text, 4, RelArm64.PageBaseRel21, counter);
        builder.Relocate(text, 8, RelArm64.PageOffset12L, counter);
        builder.Relocate(text, 16, RelArm64.PageOffset12L, counter);
        var initializers = builder.ReadOnlyData(".CRT$XDU", [4, 0, 0, 0, 0, 0, 0, 0], 8);
        builder.Relocate(initializers, 0, RelArm64.Addr64, text);

        var image = Load(Link(
            static o => o with { Architecture = TargetArchitecture.Arm64, ExportedSymbols = ["counter", "__dyn_tls_init", "__dyn_tls_on_demand_init"] },
            builder.Build()));
        var emulator = CreateEmulator(image);
        var initialize = emulator.AddressOf(image.Exports["__dyn_tls_init"]);
        var onDemand = emulator.AddressOf(image.Exports["__dyn_tls_on_demand_init"]);
        uint Counter() => emulator.Read32(emulator.AddressOf(image.Exports["counter"]));
        const ulong ThreadAttach = 2, ProcessAttach = 1;

        // The loader calls the function for every new thread; other notifications do nothing.
        emulator.Call(initialize, 0, ProcessAttach, 0);
        Assert.Equal(0u, Counter());
        emulator.Call(initialize, 0, ThreadAttach, 0);
        Assert.Equal(1u, Counter());

        // Compiled code calls the second function before it first uses a thread-local variable.
        emulator.Call(onDemand);
        Assert.Equal(1u, Counter());

        // Another thread has a TLS block of its own, and with it its own guard.
        UseThread(emulator, 1);
        emulator.Call(onDemand);
        emulator.Call(initialize, 0, ThreadAttach, 0);
        Assert.Equal(2u, Counter());
    }

    private sealed class ProgramExitException(int code) : Exception
    {
        public int Code { get; } = code;
    }

    private static ulong Mask(int size) => size == 8 ? ulong.MaxValue : uint.MaxValue;

    /// <summary>An object whose <c>wmain</c> returns immediately.</summary>
    private static byte[] EmptyMain()
    {
        var builder = new CoffObjectBuilder(CoffMachine.Arm64);
        builder.External("wmain", builder.Text(".text$mn", Code.Arm64(Ret)));
        return builder.Build();
    }

    /// <summary>Links a program that contains the given function of the runtime support and locates it.</summary>
    private (Arm64Emulator Emulator, ulong Address) EmulatorFor(string function)
    {
        var image = Load(Link(o => o with { Architecture = TargetArchitecture.Arm64, ExportedSymbols = [function] }, EmptyMain()));
        var emulator = CreateEmulator(image);
        return (emulator, emulator.AddressOf(image.Exports[function]));
    }

    private static Arm64Emulator CreateEmulator(PeImage image)
    {
        var emulator = new Arm64Emulator(image);
        UseThread(emulator, 0);
        return emulator;
    }

    /// <summary>
    /// Sets up what Windows provides per thread: x18 points to the thread environment block, which holds the
    /// array of TLS blocks at offset 0x58; the program is the first module, with index 0.
    /// </summary>
    private static void UseThread(Arm64Emulator emulator, ulong thread)
    {
        var environment = 0x4000_0000 + thread * 0x10_0000;
        emulator.X[18] = environment;
        emulator.Write64(environment + 0x58, environment + 0x1000);
        emulator.Write64(environment + 0x1000, environment + 0x2000);
    }
}
