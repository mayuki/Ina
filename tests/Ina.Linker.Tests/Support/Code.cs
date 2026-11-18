using Ina.Linker.Elf;

namespace Ina.Linker.Tests.Support;

/// <summary>Hand-assembled x86-64 snippets shared by the tests.</summary>
internal static class Code
{
    /// <summary>Offset of the rel32 operand inside <see cref="CallAndReturn"/>.</summary>
    public const int CallOperand = 5;

    /// <summary><c>sub rsp, 8 ; call rel32 ; add rsp, 8 ; ret</c> - returns whatever the callee returns.</summary>
    public static byte[] CallAndReturn => [0x48, 0x83, 0xec, 0x08, 0xe8, 0x00, 0x00, 0x00, 0x00, 0x48, 0x83, 0xc4, 0x08, 0xc3];

    /// <summary><c>mov eax, value ; ret</c></summary>
    public static byte[] Return(int value) => [0xb8, (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24), 0xc3];

    /// <summary>An object with a single global function.</summary>
    public static byte[] Function(string name, byte[] code, string section = ".text")
    {
        var builder = new ElfObjectBuilder();
        builder.Global(name, builder.Text(section, code));
        return builder.Build();
    }

    /// <summary>An object whose <c>main</c> calls <paramref name="callee"/> and returns its result.</summary>
    public static byte[] MainCalling(string callee)
    {
        var builder = new ElfObjectBuilder();
        var text = builder.Text(".text", CallAndReturn);
        builder.Global("main", text);
        builder.Relocate(text, CallOperand, RelocX64.Plt32, callee, -4);
        return builder.Build();
    }
}
