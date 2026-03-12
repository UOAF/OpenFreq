using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenFreq.Common.Rtp;

namespace OpenFreq.Common;

/// <summary>
/// Manages a pool of per-SSRC jitter buffers. Stale sources are pruned automatically.
/// </summary>
public sealed class RtpJitterBufferPool : IDisposable
{
    private readonly ILogger<RtpJitterBufferPool> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly bool _opusEnabled;
    private readonly int _initialBufferMs;

    private readonly ConcurrentDictionary<uint, RtpSourceContext> _sources = new();

    /// <summary>
    /// How long a source must be silent before it is pruned from the pool.
    /// </summary>
    public int SourceTimeoutMs { get; set; } = 5 * 60_000;

    /// <summary>Fired when a new SSRC is seen for the first time.</summary>
    public event Action<uint>? SourceAdded;

    /// <summary>Fired just before a stale source is removed.</summary>
    public event Action<uint>? SourceExpired;

    public RtpJitterBufferPool(ILoggerFactory loggerFactory, bool opusEnabled, int initialBufferMs)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<RtpJitterBufferPool>();
        _opusEnabled = opusEnabled;
        _initialBufferMs = initialBufferMs;
    }

    /// <summary>
    /// Route an incoming packet to the correct per-SSRC context,
    /// creating one if this is the first packet from that source.
    /// </summary>
    public void AddPacket(RtpPacket packet)
    {
        var context = _sources.GetOrAdd(packet.Ssrc, ssrc =>
        {
            _logger.LogInformation("New RTP source: SSRC={Ssrc:X8}", ssrc);
            var ctx = new RtpSourceContext(ssrc, _loggerFactory, _opusEnabled, _initialBufferMs);
            SourceAdded?.Invoke(ssrc);
            return ctx;
        });

        context.LastActivityTicks = Stopwatch.GetTimestamp();
        context.JitterBuffer.AddPacket(packet);
    }

    /// <summary>
    /// Returns a point-in-time snapshot of all currently active source contexts.
    /// Safe to iterate while new packets arrive concurrently.
    /// </summary>
    public IReadOnlyList<RtpSourceContext> GetActiveSources()
        => _sources.Values.ToList();

    /// <summary>
    /// Removes sources that have not received a packet within <see cref="SourceTimeoutMs"/>.
    /// </summary>
    public void PruneStale()
    {
        var threshold = (long)(SourceTimeoutMs / 1000.0 * Stopwatch.Frequency);
        var now = Stopwatch.GetTimestamp();

        foreach (var (ssrc, context) in _sources)
        {
            if (now - context.LastActivityTicks <= threshold)
                continue;

            if (_sources.TryRemove(ssrc, out var removed))
            {
                _logger.LogInformation("Pruned stale source SSRC={Ssrc:X8}", ssrc);
                SourceExpired?.Invoke(ssrc);
                removed.Dispose();
            }
        }
    }

    /// <summary>
    /// Override the jitter buffer target size on all current (and future) sources.
    /// </summary>
    public void SetAllBufferSizes(int milliseconds)
    {
        foreach (var context in _sources.Values)
            context.JitterBuffer.SetTargetBufferSize(milliseconds);
    }

    /// <summary>
    /// Aggregate statistics across all active sources.
    /// </summary>
    public (int received, int lost, int late, int duplicate, int played,
        double lossPercent, double jitterMs, double bufferMs, int buffered) GetStatistics()
    {
        var sources = GetActiveSources();
        if (sources.Count == 0)
            return (0, 0, 0, 0, 0, 0.0, 0.0, 0.0, 0);

        int received = 0, lost = 0, late = 0, duplicate = 0, played = 0, buffered = 0;
        double jitterSum = 0, bufferSum = 0;

        foreach (var ctx in sources)
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
        }

        double lossPercent = (received + lost) > 0
            ? 100.0 * lost / (received + lost)
            : 0.0;

        return (received, lost, late, duplicate, played,
            lossPercent,
            jitterSum  / sources.Count,   // average jitter across sources
            bufferSum  / sources.Count,   // average buffer size across sources
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