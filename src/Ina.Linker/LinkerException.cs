namespace Ina.Linker;

/// <summary>Raised when the inputs cannot be linked (malformed input, undefined symbols, unsupported features).</summary>
public sealed class LinkerException : Exception
{
    public LinkerException(string message)
        : base(message)
    {
    }

    public LinkerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
