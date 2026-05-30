using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using OpenFreq.Common;
using OpenFreq.Common.Rtp;
using static OpenFreq.Common.RtpAudioReceiver;

namespace OpenFreq.Common.Tests;

public class RtpJitterBufferPoolTests
{
    private const uint SamplesPerFrame = 960;

    private static RtpJitterBufferPool CreatePool(out Channel<AudioReceivedEventArgs> player)
    {
        player = Channel.CreateUnbounded<AudioReceivedEventArgs>();
        // opusEnabled: false avoids creating native Opus decoders in the per-source contexts.
        return new RtpJitterBufferPool(
            NullLoggerFactory.Instance,
            CancellationToken.None,
            player.Writer,
            opusEnabled: false,
            initialBufferMs: 40);
    }

    private static RtpPacket MakePacket(ushort seq, uint ssrc, uint timestamp)
        => new() { SequenceNumber = seq, Ssrc = ssrc, Timestamp = timestamp, Payload = [0x01] };

    [Fact]
    public void AddPacket_NewSsrc_RaisesSourceAdded()
    {
        using var pool = CreatePool(out _);
        var added = new List<uint>();
        pool.SourceAdded += s => added.Add(s);

        pool.AddPacket(MakePacket(1, ssrc: 0xAAAA, timestamp: 0));

        Assert.Equal([0xAAAAu], added);
    }

    [Fact]
    public void AddPacket_SameSsrcTwice_SourceAddedOnce()
    {
        using var pool = CreatePool(out _);
        int addedCount = 0;
        pool.SourceAdded += _ => addedCount++;

        pool.AddPacket(MakePacket(1, 0xAAAA, 0));
        pool.AddPacket(MakePacket(2, 0xAAAA, SamplesPerFrame));

        Assert.Equal(1, addedCount);
        Assert.Single(pool.GetActiveSources());
    }

    [Fact]
    public void AddPacket_DistinctSsrcs_CreatesSeparateSources()
    {
        using var pool = CreatePool(out _);

        pool.AddPacket(MakePacket(1, 0x1111, 0));
        pool.AddPacket(MakePacket(1, 0x2222, 0));

        Assert.Equal(2, pool.GetActiveSources().Count());
    }

    [Fact]
    public void GetStatistics_NoSources_AllZero()
    {
        using var pool = CreatePool(out _);

        var (received, lost, late, duplicate, played, lossPercent, jitterMs, bufferMs, buffered)
            = pool.GetStatistics();

        Assert.Equal(0, received);
        Assert.Equal(0, lost);
        Assert.Equal(0, late);
        Assert.Equal(0, duplicate);
        Assert.Equal(0, played);
        Assert.Equal(0.0, lossPercent);
        Assert.Equal(0.0, jitterMs);
        Assert.Equal(0.0, bufferMs);
        Assert.Equal(0, buffered);
    }

    [Fact]
    public void GetStatistics_AggregatesAcrossSources()
    {
        using var pool = CreatePool(out _);
        pool.AddPacket(MakePacket(1, 0x1111, 0));
        pool.AddPacket(MakePacket(1, 0x2222, 0));

        var stats = pool.GetStatistics();

        Assert.Equal(2, stats.received);
        Assert.Equal(2, stats.buffered);
    }

    [Fact]
    public void GetPerSourceStatistics_ReturnsEntryPerSource()
    {
        using var pool = CreatePool(out _);
        pool.AddPacket(MakePacket(1, 0x1111, 0));
        pool.AddPacket(MakePacket(1, 0x2222, 0));

        var perSource = pool.GetPerSourceStatistics();

        Assert.Equal(2, perSource.Count);
        Assert.Contains(perSource, s => s.ssrc == 0x1111);
        Assert.Contains(perSource, s => s.ssrc == 0x2222);
        Assert.All(perSource, s => Assert.Equal(1, s.received));
    }

    [Fact]
    public void PruneStale_RecentSource_NotRemoved()
    {
        using var pool = CreatePool(out _);
        pool.AddPacket(MakePacket(1, 0xAAAA, 0));

        pool.PruneStale(Stopwatch.GetTimestamp());

        Assert.Single(pool.GetActiveSources());
    }

    [Fact]
    public void PruneStale_StaleSource_RemovedAndRaisesSourceExpired()
    {
        using var pool = CreatePool(out _);
        pool.SourceTimeoutMs = 1; // 1ms timeout
        var expired = new List<uint>();
        pool.SourceExpired += s => expired.Add(s);

        pool.AddPacket(MakePacket(1, 0xAAAA, 0));

        // Far future timestamp guarantees the source exceeds the 1ms timeout.
        long farFuture = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
        pool.PruneStale(farFuture);

        Assert.Empty(pool.GetActiveSources());
        Assert.Equal([0xAAAAu], expired);
    }

    [Fact]
    public void Dispose_ClearsSources()
    {
        var pool = CreatePool(out _);
        pool.AddPacket(MakePacket(1, 0xAAAA, 0));

        pool.Dispose();

        Assert.Empty(pool.GetActiveSources());
    }
}
