using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace OpenFreqServer;

public class ClientSession(string id, string displayName, WebSocket webSocket, string ip): IDisposable
{
    private int _disposed;
    public bool IsDisposed => _disposed == 1;
    public string Id { get; } = id;
    public WebSocket WebSocket { get; } = webSocket;
    public bool IsAuthenticated { get; set; }
    public ConcurrentDictionary<int, FrequencyClientStatus> CurrentFrequencies { get; } = new();
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public string? DisplayName {get; set;} = displayName;
    public string Ip {get; set;} = ip;
    public SemaphoreSlim SendLock { get; } = new(1, 1);

    public enum FrequencyClientStatus
    {
        Transmitting, Receiving
    }

    public void UpdateActivity()
    {
        LastActivity = DateTime.UtcNow;
    }
    
    public void Dispose()
    {
        // Atomically sets the _disposed flag - if it was != 0 already, we have already cleaned up
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        SendLock.Dispose();
        WebSocket.Dispose();
    }
}
