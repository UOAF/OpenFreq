using System.Text.Json.Serialization;

namespace OpenFreq.Common;

public class PeerData
{
    public enum PeerStatus
    {
        Receiving,
        Transmitting
    }

    public PeerData(string id, string? name, PeerStatus status, bool is3d = false)
    {
        Id = id;
        Name = name;
        Status = status;
        Is3d = is3d;
    }
    [JsonPropertyName("id")]
    public string Id { get; set; }
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    [JsonPropertyName("status")]
    public PeerStatus Status { get; set; }
    [JsonPropertyName("3d")]
    public bool Is3d { get; set; }
}
