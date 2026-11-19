using Ina.Linker.Pe;

namespace Ina.Linker.Tests.Support;

/// <summary>Hand-assembled x86-64 snippets for the Windows calling convention, shared by the PE tests.</summary>
internal static class WindowsCode
{
    /// <summary>Offset of the rel32 operand inside <see cref="CallAndReturn"/>.</summary>
    public const int CallOperand = 5;

    /// <summary>
    /// <c>sub rsp, 0x28 ; call rel32 ; add rsp, 0x28 ; ret</c> - returns whatever the callee returns. The frame is the
    /// shadow space that every caller provides on Windows, plus alignment.
    /// </summary>
    public static byte[] CallAndReturn => [0x48, 0x83, 0xec, 0x28, 0xe8, 0x00, 0x00, 0x00, 0x00, 0x48, 0x83, 0xc4, 0x28, 0xc3];

    /// <summary><c>mov eax, value ; ret</c></summary>
    public static byte[] Return(int value) => [0xb8, (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24), 0xc3];

    /// <summary>An object with a single global function.</summary>
    public static byte[] Function(string name, byte[] code, string section = ".text$mn")
    {
        var builder = new CoffObjectBuilder();
        builder.External(name, builder.Text(section, code));
        return builder.Build();
    }

    /// <summary>An object whose <c>wmain</c> returns a constant.</summary>
    public static byte[] MainReturning(int exitCode) => Function("wmain", Return(exitCode));

    /// <summary>An object whose <c>wmain</c> calls <paramref name="callee"/> and returns its result.</summary>
    public static byte[] MainCalling(string callee)
    {
        var builder = new CoffObjectBuilder();
        var text = builder.Text(".text$mn", CallAndReturn);
        builder.External("wmain", text);
        builder.Relocate(text, CallOperand, RelAmd64.Rel32, callee);
        return builder.Build();
    }

    /// <summary>
    /// An object whose <c>wmain</c> prints the NUL-terminated string that the symbol <paramref name="pointer"/> holds
    /// the address of, using <c>puts</c> of the C runtime DLL.
    /// </summary>
    public static CoffObjectBuilder MainPrinting(string pointer)
    {
        var builder = new CoffObjectBuilder();
        var text = builder.Text(".text$mn",
            0x48, 0x83, 0xec, 0x28,                   // sub  rsp, 0x28
            0x48, 0x8b, 0x0d, 0x00, 0x00, 0x00, 0x00, // mov  rcx, [rip + pointer]
            0xe8, 0x00, 0x00, 0x00, 0x00,             // call puts
            0x33, 0xc0,                               // xor  eax, eax
            0x48, 0x83, 0xc4, 0x28,                   // add  rsp, 0x28
            0xc3);                                    // ret
        builder.External("wmain", text);
        builder.Relocate(text, 7, RelAmd64.Rel32, pointer);
        builder.Relocate(text, 12, RelAmd64.Rel32, "puts");
        return builder;
    }
}
