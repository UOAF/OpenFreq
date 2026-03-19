using System.Text.Json.Serialization;

namespace OpenFreq.Common;

public class PeerData
{
    public enum PeerStatus
    {
        Receiving,
        Transmitting
    }

    public PeerData(string id, string? name, PeerStatus status)
    {
        Id = id;
        Name = name;
        Status = status;
    }
    [JsonPropertyName("id")]
    public string Id {get; set;}
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    [JsonPropertyName("status")]
    public PeerStatus Status { get; set; }
}