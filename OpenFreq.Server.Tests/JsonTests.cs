using OpenFreqServer;
using OpenFreqServer.Json;

namespace OpenFreq.Server.Tests;

public class JsonTests
{
    [Fact]
    public void Serialize_ServerConfig_RoundTrips()
    {
        var config = new ServerConfig
        {
            ServerPassword = "secret",
            WebSocketPort = 1111,
            AudioPort = 2222,
            MaxClientsPerChannel = 7,
            MaxChannelsPerClient = 3,
            EnableOpusCompression = false,
            BroadcastPeerUpdates = false
        };

        var json = Json.Instance.Serialize(config);
        var back = Json.Instance.Deserialize<ServerConfig>(json);

        Assert.NotNull(back);
        Assert.Equal("secret", back.ServerPassword);
        Assert.Equal(1111, back.WebSocketPort);
        Assert.Equal(2222, back.AudioPort);
        Assert.Equal(7, back.MaxClientsPerChannel);
        Assert.Equal(3, back.MaxChannelsPerClient);
        Assert.False(back.EnableOpusCompression);
        Assert.False(back.BroadcastPeerUpdates);
    }

    [Fact]
    public void Deserialize_PartialJson_UsesDefaultsForMissing()
    {
        var back = Json.Instance.Deserialize<ServerConfig>("{\"websocketPort\": 8000}");

        Assert.NotNull(back);
        Assert.Equal(8000, back.WebSocketPort);
        Assert.Equal(9988, back.AudioPort);          // default
        Assert.Equal(50, back.MaxClientsPerChannel); // default
        Assert.True(back.EnableOpusCompression);     // default
    }

    [Fact]
    public void Serialize_DefaultConfig_RoundTripsToDefaults()
    {
        var json = Json.Instance.Serialize(new ServerConfig());
        var back = Json.Instance.Deserialize<ServerConfig>(json);

        Assert.NotNull(back);
        Assert.Null(back.ServerPassword);
        Assert.Equal(9987, back.WebSocketPort);
    }

    [Fact]
    public void Serialize_WriteIndented_ProducesMultiLineJson()
    {
        var json = Json.Instance.Serialize(new ServerConfig());
        Assert.Contains("\n", json);
    }

    [Fact]
    public void SerializeToUtf8Bytes_RoundTrips()
    {
        var config = new ServerConfig { WebSocketPort = 4242 };

        byte[] bytes = Json.Instance.SerializeToUtf8Bytes(config);
        var back = Json.Instance.Deserialize<ServerConfig>(bytes);

        Assert.NotNull(back);
        Assert.Equal(4242, back.WebSocketPort);
    }

    [Fact]
    public void Instance_IsSingleton()
    {
        Assert.Same(Json.Instance, Json.Instance);
    }
}
