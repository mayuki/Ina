using Microsoft.Extensions.Logging;

namespace Ina.Cli;

/// <summary>Writes log messages the way command line tools do: plain progress, prefixed warnings and errors.</summary>
internal sealed class ConsoleReporter(bool verbose) : ILogger
{
    private readonly Lock _lock = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= (verbose ? LogLevel.Debug : LogLevel.Information);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        lock (_lock)
        {
            switch (logLevel)
            {
                case >= LogLevel.Error:
                    Console.Error.WriteLine($"error: {message}");
                    break;
                case LogLevel.Warning:
                    Console.Error.WriteLine($"warning: {message}");
                    break;
                case LogLevel.Information:
                    Console.Out.WriteLine(message);
                    break;
                default:
                    Console.Out.WriteLine($"  {message}");
                    break;
            }
        }
    }
}
