using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace OpenFreq.Server.Tests.Integration;

/// <summary>
/// Logger factory that records every formatted message, from every category and level, for tests that
/// assert on what the server logged.
/// </summary>
public sealed class CapturingLoggerFactory : ILoggerFactory
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_messages) return _messages.ToList();
        }
    }

    /// <summary>
    /// Waits for a message that matches <paramref name="predicate"/>. The server logs from its own tasks,
    /// so a message can arrive after the client-side event the test waited on.
    /// </summary>
    public async Task<string> WaitForAsync(Func<string, bool> predicate, TimeSpan? timeout = null)
    {
        var start = Stopwatch.GetTimestamp();
        while (true)
        {
            var match = Messages.FirstOrDefault(predicate);
            if (match != null) return match;

            if (Stopwatch.GetElapsedTime(start) > (timeout ?? DefaultTimeout))
                throw new TimeoutException("No matching log message within the timeout");

            await Task.Delay(20);
        }
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }

    private sealed class CapturingLogger(CapturingLoggerFactory owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (owner._messages) owner._messages.Add(formatter(state, exception));
        }
    }
}
