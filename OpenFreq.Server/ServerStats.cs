using System.Collections.Concurrent;

namespace OpenFreq.Server;

public class ServerStats
{
    private readonly ConcurrentDictionary<string, ClientSession> _clients;
    private readonly FrequencyChannelManager _channelManager;
    private readonly DateTime _startTime;

    public ServerStats(ConcurrentDictionary<string, ClientSession> clients, FrequencyChannelManager channelManager)
    {
        _clients = clients;
        _channelManager = channelManager;
        _startTime = DateTime.UtcNow;
    }

    public int TotalClients => _clients.Count;

    public int AuthenticatedClients => _clients.Values.Count(c => c.IsAuthenticated);

    public int ActiveTransmissions => _clients.SelectMany(client => client.Value.CurrentFrequencies.Values)
        .Count(freq => freq == ClientSession.FrequencyClientStatus.Transmitting);

    public TimeSpan Uptime => DateTime.UtcNow - _startTime;

    public List<(int FrequencyKhz, int ClientCount)> GetFrequencyStats()
    {
        var stats = new List<(int FrequencyKhz, int ClientCount)>();
        var allFrequencies = _clients
            .SelectMany(client => client.Value.CurrentFrequencies.Keys)
            .Distinct()
            .ToList();

        foreach (var freq in allFrequencies)
        {
            var count = _channelManager.GetChannelCount(freq);
            stats.Add((freq, count));
        }

        return stats.OrderBy(s => s.FrequencyKhz).ToList();
    }
    
    public List<ClientSession> GetActiveClients()
    {
        return _clients.Values
            .Where(c => c.IsAuthenticated)
            .OrderBy(c => c.LastActivity)
            .ToList();
    }
}