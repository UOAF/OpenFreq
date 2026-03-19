using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace OpenFreq.Server;

public class ClientSession
{
    public string Id { get; }
    public WebSocket WebSocket { get; }
    public bool IsAuthenticated { get; set; }
    public ConcurrentDictionary<int, FrequencyClientStatus> CurrentFrequencies { get; set; } = new();
    public DateTime LastActivity { get; set; }
    public int AudioPort { get; set; }
    public string? DisplayName {get; set;}

    public ClientSession(string id, string displayName, WebSocket webSocket)
    {
        Id = id;
        DisplayName = displayName;
        WebSocket = webSocket;
        IsAuthenticated = false;
        LastActivity = DateTime.UtcNow;
        AudioPort = 0;
    }
    
    public enum FrequencyClientStatus
    {
        Transmitting, Receiving
    }

    public void UpdateActivity()
    {
        LastActivity = DateTime.UtcNow;
    }
}
