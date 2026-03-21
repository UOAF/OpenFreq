using System.Text.Json.Serialization;

namespace OpenFreqServer;

public class ServerConfig
{
    [JsonPropertyName("serverPassword")]
    public string? ServerPassword { get; set; }

    [JsonPropertyName("websocketPort")]
    public int WebSocketPort { get; set; } = 9987;

    [JsonPropertyName("audioPort")]
    public int AudioPort { get; set; } = 9988;

    [JsonPropertyName("maxClientsPerChannel")]
    public int MaxClientsPerChannel { get; set; } = 50;
    
    [JsonPropertyName("maxChannelsPerClient")]
    public int MaxChannelsPerClient { get; set; } = 10;
    
    [JsonPropertyName("opusCompression")]
    public bool EnableOpusCompression { get; set; } = true;
    
    [JsonPropertyName("broadcastPeerUpdates")]
    public bool BroadcastPeerUpdates { get; set; } = true;
}
