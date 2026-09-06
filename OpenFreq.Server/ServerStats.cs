using System.Collections.Concurrent;
using OpenFreq.Common;

namespace OpenFreqServer;

public class ServerStats(ConcurrentDictionary<string, ClientSession> clients, FrequencyChannelManager channelManager, IAudioStreamServer audioServer)
{
    private readonly DateTime _startTime = DateTime.UtcNow;

    public int TotalClients => clients.Count;

    public int AuthenticatedClients => clients.Values.Count(c => c.IsAuthenticated);

    public int ActiveTransmissions => channelManager.CountTransmitting();

    public TimeSpan Uptime => DateTime.UtcNow - _startTime;

    public List<ChannelSummary> GetFrequencyStats() => channelManager.GetChannelSummaries();

    public DateTime? GetLastRtpReceived(string clientId) => audioServer.GetLastRtpReceived(clientId);

    /// <summary>
    /// The frequencies a client is on and whether it is transmitting on each, ordered by
    /// frequency so the TUI renders a stable list.
    /// </summary>
    public List<(int FrequencyKhz, bool IsTransmitting)> GetClientFrequencies(string clientId) =>
        channelManager.GetClientChannelStates(clientId)
            .OrderBy(s => s.FrequencyKhz)
            .Select(s => (s.FrequencyKhz, s.Peer.Status == PeerData.PeerStatus.Transmitting))
            .ToList();

    public List<ClientSession> GetActiveClients()
    {
        return clients.Values
            .Where(c => c.IsAuthenticated)
            .OrderBy(c => c.LastActivity)
            .ToList();
    }
}
