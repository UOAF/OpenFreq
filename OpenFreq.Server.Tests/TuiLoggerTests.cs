using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenFreqServer;

namespace OpenFreq.Server.Tests;

public class TuiLoggerTests
{
    private static TuiLogger Create(ConcurrentQueue<TuiLogMessage> q, int max = 100)
        => new(q, max);

    [Fact]
    public void Log_EnqueuesFormattedMessage()
    {
        var q = new ConcurrentQueue<TuiLogMessage>();
        var logger = Create(q);

        logger.Log(LogLevel.Information, default, "state", null, (s, _) => $"hello {s}");

        Assert.True(q.TryDequeue(out var msg));
        Assert.NotNull(msg);
        Assert.Equal(LogLevel.Information, msg.Level);
        Assert.Equal("hello state", msg.Message);
    }

    [Fact]
    public void Log_WithException_AppendsExceptionMessage()
    {
        var q = new ConcurrentQueue<TuiLogMessage>();
        var logger = Create(q);

        logger.Log(LogLevel.Error, default, "x", new InvalidOperationException("boom"), (s, _) => "failed");

        Assert.True(q.TryDequeue(out var msg));
        Assert.NotNull(msg);
        Assert.Contains("failed", msg.Message);
        Assert.Contains("boom", msg.Message);
    }

    [Fact]
    public void Log_LevelNone_NotEnqueued()
    {
        var q = new ConcurrentQueue<TuiLogMessage>();
        var logger = Create(q);

        logger.Log(LogLevel.None, default, "x", null, (s, _) => "ignored");

        Assert.Empty(q);
    }

    [Fact]
    public void Log_ExceedingMax_TrimsToLimit()
    {
        var q = new ConcurrentQueue<TuiLogMessage>();
        var logger = Create(q, max: 3);

        for (int i = 0; i < 10; i++)
            logger.Log(LogLevel.Information, default, i, null, (s, _) => s.ToString()!);

        Assert.Equal(3, q.Count);
    }

    [Fact]
    public void IsEnabled_FalseOnlyForNone()
    {
        var logger = Create(new ConcurrentQueue<TuiLogMessage>());
        Assert.True(logger.IsEnabled(LogLevel.Trace));
        Assert.True(logger.IsEnabled(LogLevel.Critical));
        Assert.False(logger.IsEnabled(LogLevel.None));
    }

    [Fact]
    public void BeginScope_ReturnsNull()
    {
        var logger = Create(new ConcurrentQueue<TuiLogMessage>());
        Assert.Null(logger.BeginScope("scope"));
    }

    [Fact]
    public void Provider_CreateLogger_ReturnsTuiLoggerWritingToSharedQueue()
    {
        var q = new ConcurrentQueue<TuiLogMessage>();
        var provider = new TuiLoggerProvider(q);

        var logger = provider.CreateLogger("cat");
        logger.Log(LogLevel.Warning, default, "s", null, (s, _) => "via provider");
        provider.Dispose();

        Assert.True(q.TryDequeue(out var msg));
        Assert.Equal("via provider", msg!.Message);
    }
}
