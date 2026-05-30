using Microsoft.Extensions.Logging;

namespace OpenFreq.Server.Tests.Integration;

/// <summary>
/// Logger factory whose loggers report every level enabled but discard output. Lets the audio
/// integration tests exercise the server's <c>IsEnabled(...)</c>-gated logging paths (which
/// <see cref="Microsoft.Extensions.Logging.Abstractions.NullLogger"/> skips) without emitting noise.
/// </summary>
public sealed class EnabledLoggerFactory : ILoggerFactory
{
    public static readonly EnabledLoggerFactory Instance = new();

    public ILogger CreateLogger(string categoryName) => NoopLogger.Instance;
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }

    private sealed class NoopLogger : ILogger
    {
        public static readonly NoopLogger Instance = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            // Force the message to materialize so any work in the formatter is exercised, then drop it.
            _ = formatter(state, exception);
        }
    }
}
