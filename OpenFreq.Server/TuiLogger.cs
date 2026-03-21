using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace OpenFreqServer;

public class TuiLogMessage
{
    public DateTime Timestamp { get; init; }
    public LogLevel Level { get; init; }
    public string Message { get; init; } = string.Empty;
}

public class TuiLogger(ConcurrentQueue<TuiLogMessage> logMessages, int maxMessages = 100)
    : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var message = formatter(state, exception);
        if (exception != null)
        {
            message += $" | Exception: {exception.Message}";
        }

        logMessages.Enqueue(new TuiLogMessage
        {
            Timestamp = DateTime.UtcNow,
            Level = logLevel,
            Message = message
        });

        // Keep only the last N messages
        while (logMessages.Count > maxMessages)
        {
            logMessages.TryDequeue(out _);
        }
    }
}

public class TuiLoggerProvider(ConcurrentQueue<TuiLogMessage> logMessages, int maxMessages = 100)
    : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName)
    {
        return new TuiLogger(logMessages, maxMessages);
    }

    public void Dispose() { }
}
