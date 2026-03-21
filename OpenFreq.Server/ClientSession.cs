using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace OpenFreqServer;

public class ClientSession(string id, string displayName, WebSocket webSocket, string ip)
{
    public string Id { get; } = id;
    public WebSocket WebSocket { get; } = webSocket;
    public bool IsAuthenticated { get; set; }
    public ConcurrentDictionary<int, FrequencyClientStatus> CurrentFrequencies { get; } = new();
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public string? DisplayName {get; set;} = displayName;
    public string Ip {get; set;} = ip;

    public enum FrequencyClientStatus
    {
        Transmitting, Receiving
    }

    public void UpdateActivity()
    {
        LastActivity = DateTime.UtcNow;
    }
}
