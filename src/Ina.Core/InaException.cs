namespace Ina;

/// <summary>An error that is reported to the user as a build failure rather than as a crash.</summary>
public sealed class InaException : Exception
{
    public InaException(string message)
        : base(message)
    {
    }

    public InaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
