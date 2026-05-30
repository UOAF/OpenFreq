using OpenFreqServer;

namespace OpenFreq.Server.Tests;

public class ServerConfigTests
{
    [Fact]
    public void DefaultWebSocketPort_Is9987()
    {
        var config = new ServerConfig();
        Assert.Equal(9987, config.WebSocketPort);
    }

    [Fact]
    public void DefaultAudioPort_Is9988()
    {
        var config = new ServerConfig();
        Assert.Equal(9988, config.AudioPort);
    }

    [Fact]
    public void DefaultMaxClientsPerChannel_Is50()
    {
        var config = new ServerConfig();
        Assert.Equal(50, config.MaxClientsPerChannel);
    }

    [Fact]
    public void DefaultMaxChannelsPerClient_Is10()
    {
        var config = new ServerConfig();
        Assert.Equal(10, config.MaxChannelsPerClient);
    }

    [Fact]
    public void DefaultEnableOpusCompression_IsTrue()
    {
        var config = new ServerConfig();
        Assert.True(config.EnableOpusCompression);
    }

    [Fact]
    public void DefaultBroadcastPeerUpdates_IsTrue()
    {
        var config = new ServerConfig();
        Assert.True(config.BroadcastPeerUpdates);
    }

    [Fact]
    public void DefaultServerPassword_IsNull()
    {
        var config = new ServerConfig();
        Assert.Null(config.ServerPassword);
    }
}
