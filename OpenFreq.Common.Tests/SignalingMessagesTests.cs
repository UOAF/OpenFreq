using OpenFreq.Common;

namespace OpenFreq.Common.Tests;

public class SignalingMessagesTests
{
    [Fact]
    public void ChannelStateMessage_Peer_ConstructorSetsFields()
    {
        var peer = new ChannelStateMessage.Peer("peer-1", "Viper");
        Assert.Equal("peer-1", peer.Id);
        Assert.Equal("Viper", peer.DisplayName);
    }

    [Fact]
    public void ChannelStateMessage_Defaults_EmptyPeers()
    {
        var msg = new ChannelStateMessage { FrequencyKhz = 251000 };
        Assert.Equal(251000, msg.FrequencyKhz);
        Assert.Empty(msg.Peers);
    }

    [Fact]
    public void SignalingMessage_Defaults_TypeEmptyPayloadNull()
    {
        var msg = new SignalingMessage();
        Assert.Equal(string.Empty, msg.Type);
        Assert.Null(msg.Payload);
    }

    [Fact]
    public void SuccessMessage_Defaults()
    {
        var msg = new SuccessMessage();
        Assert.Equal(string.Empty, msg.Message);
        Assert.Null(msg.PeerId);
        Assert.Null(msg.AudioPort);
        Assert.False(msg.OpusCompressionEnabled);
        Assert.Empty(msg.FrequenciesPeers);
    }

    [Fact]
    public void ErrorMessage_DefaultErrorEmpty()
    {
        Assert.Equal(string.Empty, new ErrorMessage().Error);
    }

    [Fact]
    public void PeerJoinedMessage_DefaultsEmpty()
    {
        var msg = new PeerJoinedMessage();
        Assert.Equal(string.Empty, msg.PeerId);
        Assert.Equal(string.Empty, msg.PeerDisplayName);
        Assert.Equal(0, msg.FrequencyKhz);
    }

    [Fact]
    public void AllPeersStatusMessage_DefaultEmpty()
    {
        Assert.Empty(new AllPeersStatusMessage().FrequenciesPeers);
    }
}
