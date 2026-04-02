using Microsoft.Extensions.Logging;
using OpenFreq.Common.Rtp;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using static OpenFreq.Common.RtpAudioReceiver;

namespace OpenFreq.Common;

/// <summary>
/// Manages a pool of per-SSRC jitter buffers. Stale sources are pruned automatically.
/// </summary>
public sealed class RtpJitterBufferPool : IDisposable
{
    private readonly ILogger<RtpJitterBufferPool> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly CancellationToken _cancelled;
    private readonly ChannelWriter<AudioReceivedEventArgs> _player;
    private readonly bool _opusEnabled;
    private readonly int _initialBufferMs;

    private readonly Dictionary<uint, RtpSourceContext> _sources = new();

    /// <summary>
    /// How long a source must be silent before it is pruned from the pool.
    /// </summary>
    public int SourceTimeoutMs { get; set; } = 5 * 60_000;

    /// <summary>Fired when a new SSRC is seen for the first time.</summary>
    public event Action<uint>? SourceAdded;

    /// <summary>Fired just before a stale source is removed.</summary>
    public event Action<uint>? SourceExpired;

    public RtpJitterBufferPool(
        ILoggerFactory loggerFactory,
        CancellationToken ct,
        ChannelWriter<AudioReceivedEventArgs> player,
        bool opusEnabled,
        int initialBufferMs)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<RtpJitterBufferPool>();
        _cancelled = ct;
        _player = player;
        _opusEnabled = opusEnabled;
        _initialBufferMs = initialBufferMs;
    }

    /// <summary>
    /// Route an incoming packet to the correct per-SSRC context,
    /// creating one if this is the first packet from that source.
    /// </summary>
    public void AddPacket(RtpPacket packet)
    {
        var ssrc = packet.Ssrc;
        RtpSourceContext? src;
        if (!_sources.TryGetValue(ssrc, out src))
        {
            _logger.LogInformation("New RTP source: SSRC={Ssrc:X8}", ssrc);
            src = new RtpSourceContext(ssrc, _loggerFactory, _cancelled, _player, _opusEnabled, _initialBufferMs);
            _sources.Add(ssrc, src);
            SourceAdded?.Invoke(ssrc);
        }

        src.LastActivityTicks = Stopwatch.GetTimestamp();
        src.JitterBuffer.AddPacket(packet);
    }

    /// <summary>
    /// Returns a point-in-time snapshot of all currently active source contexts.
    /// Safe to iterate while new packets arrive concurrently.
    /// </summary>
    public IEnumerable<RtpSourceContext> GetActiveSources()
        => _sources.Values;

    /// <summary>
    /// Removes sources that have not received a packet within <see cref="SourceTimeoutMs"/>.
    /// </summary>
    public void PruneStale(long now)
    {
        var threshold = (long)(SourceTimeoutMs / 1000.0 * Stopwatch.Frequency);
        List<uint> toRemove = [];
        foreach (var (ssrc, context) in _sources)
        {
            if (now - context.LastActivityTicks <= threshold)
                continue;

            toRemove.Add(ssrc);
        }
        foreach (var r in toRemove)
        {
            _logger.LogInformation("Pruned stale source SSRC={Ssrc:X8}", r);
            SourceExpired?.Invoke(r);
            _sources.Remove(r, out var removed);
            removed!.Dispose();
        }
    }

    /// <summary>
    /// Aggregate statistics across all active sources.
    /// </summary>
    public (int received, int lost, int late, int duplicate, int played,
        double lossPercent, double jitterMs, double bufferMs, int buffered) GetStatistics()
    {
        int received = 0, lost = 0, late = 0, duplicate = 0, played = 0, buffered = 0;
        double jitterSum = 0, bufferSum = 0;
        int numSources = 0;

        foreach (var ctx in GetActiveSources())
        {
            var s = ctx.JitterBuffer.GetStatistics();
            received  += s.received;
            lost      += s.lost;
            late      += s.late;
            duplicate += s.duplicate;
            played    += s.played;
            buffered  += s.buffered;
            jitterSum += s.jitterMs;
            bufferSum += s.bufferMs;
            numSources++;
        }

        if (numSources == 0)
            return (0, 0, 0, 0, 0, 0.0, 0.0, 0.0, 0);

        double lossPercent = (received + lost) > 0
            ? 100.0 * lost / (received + lost)
            : 0.0;

        return (received, lost, late, duplicate, played,
            lossPercent,
            jitterSum  / numSources,   // average jitter across sources
            bufferSum  / numSources,   // average buffer size across sources
            buffered);
    }

    /// <summary>
    /// Per-source statistics for diagnostics
    /// </summary>
    public IReadOnlyList<(uint ssrc, int received, int lost, double lossPercent, double jitterMs, double bufferMs, int buffered)>
        GetPerSourceStatistics()
    {
        return GetActiveSources()
            .Select(ctx =>
            {
                var s = ctx.JitterBuffer.GetStatistics();
                return (ctx.Ssrc, s.received, s.lost, s.lossPercent, s.jitterMs, s.bufferMs, s.buffered);
            })
            .ToList();
    }

    public void Dispose()
    {
        foreach (var (_, context) in _sources)
            context.Dispose();
        _sources.Clear();
    }
}