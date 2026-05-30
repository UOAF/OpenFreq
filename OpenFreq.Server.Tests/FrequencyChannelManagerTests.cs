using OpenFreq.Common;
using OpenFreqServer;

namespace OpenFreq.Server.Tests;

public class FrequencyChannelManagerTests
{
    private static FrequencyChannelManager Create() => new();

    [Fact]
    public void JoinChannel_NewChannel_ReturnsTrue()
    {
        var mgr = Create();
        Assert.True(mgr.JoinChannel(251000, "client-1", "Viper"));
    }

    [Fact]
    public void JoinChannel_SameClientTwice_SecondReturnsFalse()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");
        Assert.False(mgr.JoinChannel(251000, "client-1", "Viper"));
    }

    [Fact]
    public void JoinChannel_TwoClientsOnSameFreq_BothSucceed()
    {
        var mgr = Create();
        Assert.True(mgr.JoinChannel(251000, "client-1", "Viper"));
        Assert.True(mgr.JoinChannel(251000, "client-2", "Maverick"));
    }

    [Fact]
    public void JoinChannel_SameClientDifferentFreqs_BothSucceed()
    {
        var mgr = Create();
        Assert.True(mgr.JoinChannel(251000, "client-1", "Viper"));
        Assert.True(mgr.JoinChannel(135100, "client-1", "Viper"));
    }

    [Fact]
    public void LeaveChannel_ExistingClient_ReturnsTrue()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");
        Assert.True(mgr.LeaveChannel(251000, "client-1"));
    }

    [Fact]
    public void LeaveChannel_ClientNotInChannel_ReturnsFalse()
    {
        var mgr = Create();
        Assert.False(mgr.LeaveChannel(251000, "client-1"));
    }

    [Fact]
    public void LeaveChannel_WrongFrequency_ReturnsFalse()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");
        Assert.False(mgr.LeaveChannel(135100, "client-1"));
    }

    [Fact]
    public void LeaveChannel_LastClient_RemovesChannelFromState()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");
        mgr.LeaveChannel(251000, "client-1");

        Assert.Equal(0, mgr.GetChannelCount(251000));
        Assert.Empty(mgr.GetClientsInChannel(251000));
    }

    [Fact]
    public void LeaveChannel_OneOfTwo_OtherRemainsInChannel()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");
        mgr.JoinChannel(251000, "client-2", "Maverick");

        mgr.LeaveChannel(251000, "client-1");

        Assert.Equal(1, mgr.GetChannelCount(251000));
        Assert.Contains("client-2", mgr.GetClientsInChannel(251000));
    }

    [Fact]
    public void LeaveAllChannels_ClientOnMultipleFreqs_RemovedFromAll()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");
        mgr.JoinChannel(135100, "client-1", "Viper");
        mgr.JoinChannel(243000, "client-1", "Viper");

        mgr.LeaveAllChannels("client-1");

        Assert.Empty(mgr.GetClientsInChannel(251000));
        Assert.Empty(mgr.GetClientsInChannel(135100));
        Assert.Empty(mgr.GetClientsInChannel(243000));
    }

    [Fact]
    public void LeaveAllChannels_OtherClientsUnaffected()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");
        mgr.JoinChannel(251000, "client-2", "Maverick");

        mgr.LeaveAllChannels("client-1");

        Assert.Contains("client-2", mgr.GetClientsInChannel(251000));
    }

    [Fact]
    public void LeaveAllChannels_ClientNotInAnyChannel_NoException()
    {
        var mgr = Create();
        mgr.LeaveAllChannels("ghost-client"); // should not throw
    }

    [Fact]
    public void GetClientsInChannel_EmptyChannel_ReturnsEmpty()
    {
        var mgr = Create();
        Assert.Empty(mgr.GetClientsInChannel(251000));
    }

    [Fact]
    public void GetClientsInChannel_WithClients_ReturnsAllClientIds()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");
        mgr.JoinChannel(251000, "client-2", "Maverick");

        var clients = mgr.GetClientsInChannel(251000);

        Assert.Equal(2, clients.Length);
        Assert.Contains("client-1", clients);
        Assert.Contains("client-2", clients);
    }

    [Fact]
    public void GetPeersInChannel_ReturnsCorrectPeerData()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");

        var peers = mgr.GetPeersInChannel(251000);

        Assert.Single(peers);
        Assert.Equal("client-1", peers[0].Id);
        Assert.Equal("Viper", peers[0].Name);
        Assert.Equal(PeerData.PeerStatus.Receiving, peers[0].Status);
    }

    [Fact]
    public void GetPeersInChannel_With3dFlag_ReflectedInPeerData()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper", is3d: true);

        var peers = mgr.GetPeersInChannel(251000);

        Assert.True(peers[0].Is3d);
    }

    [Fact]
    public void GetPeersInChannel_NonExistentFreq_ReturnsEmpty()
    {
        var mgr = Create();
        Assert.Empty(mgr.GetPeersInChannel(999999));
    }

    [Fact]
    public void GetChannelCount_NoClients_ReturnsZero()
    {
        var mgr = Create();
        Assert.Equal(0, mgr.GetChannelCount(251000));
    }

    [Fact]
    public void GetChannelCount_WithClients_ReturnsCorrectCount()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");
        mgr.JoinChannel(251000, "client-2", "Maverick");
        mgr.JoinChannel(251000, "client-3", "Iceman");

        Assert.Equal(3, mgr.GetChannelCount(251000));
    }

    [Fact]
    public void GetClientChannels_ClientOnMultipleFreqs_ReturnsAll()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");
        mgr.JoinChannel(135100, "client-1", "Viper");

        var channels = mgr.GetClientChannels("client-1");

        Assert.Equal(2, channels.Count);
        Assert.Contains(251000.0, channels);
        Assert.Contains(135100.0, channels);
    }

    [Fact]
    public void GetClientChannels_ClientNotJoined_ReturnsEmpty()
    {
        var mgr = Create();
        Assert.Empty(mgr.GetClientChannels("ghost"));
    }

    [Fact]
    public void GetClientChannel_SingleChannel_ReturnsFrequency()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");

        Assert.Equal(251000.0, mgr.GetClientChannel("client-1"));
    }

    [Fact]
    public void GetClientChannel_ClientNotJoined_ReturnsMinusOne()
    {
        var mgr = Create();
        Assert.Equal(-1.0, mgr.GetClientChannel("ghost"));
    }

    [Fact]
    public void GetAllChannelStates_MultipleChannels_ReturnsSortedByFreq()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "c1", "A");
        mgr.JoinChannel(135100, "c2", "B");
        mgr.JoinChannel(243000, "c3", "C");

        var state = mgr.GetAllChannelStates();

        var keys = state.Keys.ToList();
        Assert.Equal([135100, 243000, 251000], keys);
    }

    [Fact]
    public void GetAllChannelStates_Empty_ReturnsEmptyDict()
    {
        var mgr = Create();
        Assert.Empty(mgr.GetAllChannelStates());
    }

    [Fact]
    public void UpdateDisplayName_ClientInChannel_NameUpdated()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "OldName");

        mgr.UpdateDisplayName("client-1", "NewName");

        var peers = mgr.GetPeersInChannel(251000);
        Assert.Equal("NewName", peers[0].Name);
    }

    [Fact]
    public void UpdateDisplayName_ClientOnMultipleFreqs_NameUpdatedEverywhere()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "OldName");
        mgr.JoinChannel(135100, "client-1", "OldName");

        mgr.UpdateDisplayName("client-1", "NewName");

        Assert.Equal("NewName", mgr.GetPeersInChannel(251000)[0].Name);
        Assert.Equal("NewName", mgr.GetPeersInChannel(135100)[0].Name);
    }

    [Fact]
    public void UpdateDisplayName_ClientNotJoined_NoException()
    {
        var mgr = Create();
        mgr.UpdateDisplayName("ghost", "SomeName"); // should not throw
    }

    [Fact]
    public void UpdateIs3d_ClientInChannel_FlagUpdated()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper", is3d: false);

        mgr.UpdateIs3d(251000, "client-1", is3d: true);

        var peers = mgr.GetPeersInChannel(251000);
        Assert.True(peers[0].Is3d);
    }

    [Fact]
    public void UpdateIs3d_WrongFrequency_OtherFrequencyUnchanged()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper", is3d: false);

        mgr.UpdateIs3d(135100, "client-1", is3d: true); // wrong freq

        // 251000 entry should be unchanged
        var peers = mgr.GetPeersInChannel(251000);
        Assert.False(peers[0].Is3d);
    }

    [Fact]
    public void UpdateIs3d_ClientNotInChannel_NoException()
    {
        var mgr = Create();
        mgr.UpdateIs3d(251000, "ghost", is3d: true); // should not throw
    }

    [Fact]
    public void GetAllChannelStates_PeersInChannel_SortedByName()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "c1", "Zulu");
        mgr.JoinChannel(251000, "c2", "Alpha");
        mgr.JoinChannel(251000, "c3", "Mike");

        var peers = mgr.GetAllChannelStates()[251000];

        Assert.Equal(["Alpha", "Mike", "Zulu"], peers.Select(p => p.Name).ToList());
    }

    [Fact]
    public void UpdateDisplayName_PreservesStatus()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "OldName");

        mgr.UpdateDisplayName("client-1", "NewName");

        Assert.Equal(PeerData.PeerStatus.Receiving, mgr.GetPeersInChannel(251000)[0].Status);
    }

    [Fact]
    public void UpdateIs3d_PreservesName()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper", is3d: false);

        mgr.UpdateIs3d(251000, "client-1", is3d: true);

        Assert.Equal("Viper", mgr.GetPeersInChannel(251000)[0].Name);
    }

    [Fact]
    public void UpdateIs3d_PreservesStatus()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper", is3d: false);

        mgr.UpdateIs3d(251000, "client-1", is3d: true);

        Assert.Equal(PeerData.PeerStatus.Receiving, mgr.GetPeersInChannel(251000)[0].Status);
    }

    [Fact]
    public void JoinChannel_AfterLeave_CanRejoin()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");
        mgr.LeaveChannel(251000, "client-1");

        Assert.True(mgr.JoinChannel(251000, "client-1", "Viper"));
    }

    [Fact]
    public void GetClientChannel_MultipleChannels_ReturnsValidFrequency()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");
        mgr.JoinChannel(135100, "client-1", "Viper");

        var channel = mgr.GetClientChannel("client-1");

        Assert.True(channel == 251000.0 || channel == 135100.0);
    }

    [Fact]
    public void LeaveChannel_AfterLeaveAllChannels_ReturnsFalse()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");
        mgr.LeaveAllChannels("client-1");

        Assert.False(mgr.LeaveChannel(251000, "client-1"));
    }
}
