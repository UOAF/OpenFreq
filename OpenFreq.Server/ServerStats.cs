using System.Collections.Concurrent;

namespace OpenFreqServer;

public class ServerStats(ConcurrentDictionary<string, ClientSession> clients, FrequencyChannelManager channelManager)
{
    private readonly DateTime _startTime = DateTime.UtcNow;

    public int TotalClients => clients.Count;

    public int AuthenticatedClients => clients.Values.Count(c => c.IsAuthenticated);

    public int ActiveTransmissions => clients.SelectMany(client => client.Value.CurrentFrequencies.Values)
        .Count(freq => freq == ClientSession.FrequencyClientStatus.Transmitting);

    public TimeSpan Uptime => DateTime.UtcNow - _startTime;

    public List<(int FrequencyKhz, int ClientCount)> GetFrequencyStats()
    {
        var stats = new List<(int FrequencyKhz, int ClientCount)>();
        var allFrequencies = clients
            .SelectMany(client => client.Value.CurrentFrequencies.Keys)
            .Distinct()
            .ToList();

        foreach (var freq in allFrequencies)
        {
            var count = channelManager.GetChannelCount(freq);
            stats.Add((freq, count));
        }

        return stats.OrderBy(s => s.FrequencyKhz).ToList();
    }
    
    public List<ClientSession> GetActiveClients()
    {
        return clients.Values
            .Where(c => c.IsAuthenticated)
            .OrderBy(c => c.LastActivity)
            .ToList();
    }
}