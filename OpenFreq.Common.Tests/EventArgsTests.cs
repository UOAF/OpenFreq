using OpenFreq.Common;

namespace OpenFreq.Common.Tests;

public class EventArgsTests
{
    [Fact]
    public void ConnectionStateChangedEventArgs_SetsState()
    {
        var e = new ConnectionStateChangedEventArgs(ConnectionState.Connected);
        Assert.Equal(ConnectionState.Connected, e.State);
    }

    [Fact]
    public void AuthenticationEventArgs_SetsAllFields()
    {
        var peers = new SortedDictionary<int, List<PeerData>>();
        var e = new AuthenticationEventArgs("peer-1", peers, 9988);
        Assert.Equal("peer-1", e.PeerId);
        Assert.Same(peers, e.Peers);
        Assert.Equal(9988, e.AudioPort);
    }

    [Fact]
    public void FrequencyJoinedEventArgs_SetsFields()
    {
        var peers = new List<ChannelStateMessage.Peer> { new("id", "name") };
        var e = new FrequencyJoinedEventArgs(251000, peers);
        Assert.Equal(251000, e.FrequencyKhz);
        Assert.Same(peers, e.Peers);
    }

    [Fact]
    public void FrequencyLeftEventArgs_SetsFrequency()
    {
        var e = new FrequencyLeftEventArgs(135100);
        Assert.Equal(135100, e.FrequencyKhz);
    }

    [Fact]
    public void PeerEventArgs_SetsFields()
    {
        var e = new PeerEventArgs("peer-7", "Goose", 243000);
        Assert.Equal("peer-7", e.PeerId);
        Assert.Equal("Goose", e.PeerDisplayName);
        Assert.Equal(243000, e.FrequencyKhz);
    }

    [Fact]
    public void PeerEventArgs_AllowsNullDisplayName()
    {
        var e = new PeerEventArgs("peer-8", null, 243000);
        Assert.Null(e.PeerDisplayName);
    }

    [Fact]
    public void TransmissionStateEventArgs_SetsFields()
    {
        var e = new TransmissionStateEventArgs(251000, isTransmitting: true);
        Assert.Equal(251000, e.FrequencyKhz);
        Assert.True(e.IsTransmitting);
    }

    [Fact]
    public void PeerTransmissionEventArgs_SetsFields()
    {
        var e = new PeerTransmissionEventArgs("peer-9", "Iceman", 251000, isTransmitting: true, is3d: true);
        Assert.Equal("peer-9", e.PeerId);
        Assert.Equal("Iceman", e.PeerDisplayName);
        Assert.Equal(251000, e.FrequencyKhz);
        Assert.True(e.IsTransmitting);
        Assert.True(e.Is3d);
    }

    [Fact]
    public void AudioDataEventArgs_SetsFields()
    {
        var audio = new Memory<short>(new short[] { 1, 2, 3 });
        var md = new AudioPacketMetadata { ClientId = "c1" };
        var e = new AudioDataEventArgs("peer-1", audio, md);
        Assert.Equal("peer-1", e.PeerId);
        Assert.Equal(3, e.AudioData.Length);
        Assert.Same(md, e.Metadata);
    }

    [Fact]
    public void AllPeersStatusEventArgs_SetsAllPeers()
    {
        var all = new SortedDictionary<int, List<PeerData>>();
        var e = new AllPeersStatusEventArgs(all);
        Assert.Same(all, e.AllPeers);
    }

    [Fact]
    public void ErrorEventArgs_SetsMessage()
    {
        var e = new OpenFreq.Common.ErrorEventArgs("boom");
        Assert.Equal("boom", e.ErrorMessage);
    }
}
