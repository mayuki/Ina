using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.Extensions.Logging;

namespace Ina.Sdk;

/// <summary>
/// Forwards the log of the linker to MSBuild. Diagnostic detail becomes low importance messages, which end up in
/// binary logs and diagnostic output without cluttering a normal build.
/// </summary>
internal sealed class TaskLogger(TaskLoggingHelper log) : Microsoft.Extensions.Logging.ILogger
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    // Formatting a message for every archive member is wasted work unless a logger actually records it.
    public bool IsEnabled(LogLevel logLevel) => logLevel switch
    {
        LogLevel.None => false,
        >= LogLevel.Warning => true,
        _ => log.LogsMessagesOfImportance(ImportanceOf(logLevel)),
    };

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        switch (logLevel)
        {
            case >= LogLevel.Error:
                log.LogError(message);
                break;
            case LogLevel.Warning:
                log.LogWarning(message);
                break;
            default:
                log.LogMessage(ImportanceOf(logLevel), message);
                break;
        }
    }

    private static MessageImportance ImportanceOf(LogLevel logLevel) =>
        logLevel == LogLevel.Information ? MessageImportance.Normal : MessageImportance.Low;
}
