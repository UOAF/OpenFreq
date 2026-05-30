using System.Collections.Concurrent;
using OpenFreqServer;

namespace OpenFreq.Server.Tests.Integration;

/// <summary>
/// In-memory stand-in for <see cref="AudioStreamServer"/> used by the signaling integration
/// tests. Binds no UDP port and runs no receive loop, so the real signaling path can be
/// exercised end-to-end without touching the network audio layer. Records lifecycle calls and
/// lets tests control the RTP heartbeat that drives the idle watchdog.
/// </summary>
public sealed class FakeAudioRelay : IAudioStreamServer
{
    public const int FakePort = 19988;

    private readonly ConcurrentDictionary<string, DateTime?> _lastRtp = new();

    public ConcurrentBag<string> CreatedSessions { get; } = new();
    public ConcurrentBag<string> RemovedSessions { get; } = new();
    public bool Stopped { get; private set; }

    public int CreateAudioSession(string clientId)
    {
        CreatedSessions.Add(clientId);
        // Default heartbeat to "now" so a freshly-authenticated client is not immediately
        // considered timed out by the idle watchdog.
        _lastRtp[clientId] = DateTime.UtcNow;
        return FakePort;
    }

    public DateTime? GetLastRtpReceived(string clientId) =>
        _lastRtp.TryGetValue(clientId, out var ts) ? ts : null;

    public void RemoveSession(string clientId)
    {
        RemovedSessions.Add(clientId);
        _lastRtp.TryRemove(clientId, out _);
    }

    public void Stop() => Stopped = true;

    /// <summary>Force a client's last-RTP timestamp, e.g. to simulate an idle (stale) client.</summary>
    public void SetLastRtpReceived(string clientId, DateTime timestamp) => _lastRtp[clientId] = timestamp;
}
