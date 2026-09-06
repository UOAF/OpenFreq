using OpenFreq.Common;
using OpenFreqServer;

namespace OpenFreq.Server.Tests;

public class FrequencyChannelManagerTests
{
    private static FrequencyChannelManager Create() => new();

    /// <summary>Set transmit state without restating the 3D mode at every call site.</summary>
    private static string[]? Transmit(FrequencyChannelManager mgr, int frequencyKhz, string clientId, bool on) =>
        mgr.SetTransmissionState(frequencyKhz, clientId, on, is3d: false);

    /// <summary>Peers on a frequency, read through the same snapshot the server broadcasts.</summary>
    private static List<PeerData> PeersIn(FrequencyChannelManager mgr, int frequencyKhz) =>
        mgr.GetAllChannelStates().TryGetValue(frequencyKhz, out var peers) ? peers : [];

    [Fact]
    public void JoinChannel_NewChannel_ReturnsChannelJoined()
    {
        var mgr = Create();
        Assert.IsType<ChannelJoined>(mgr.JoinChannel(251000, "client-1", "Viper"));
    }

    [Fact]
    public void JoinChannel_SameClientTwice_ReturnsAlreadyInChannel()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");
        Assert.IsType<AlreadyInChannel>(mgr.JoinChannel(251000, "client-1", "Viper"));
    }

    [Fact]
    public void JoinChannel_AtCapacity_ReturnsChannelFull()
    {
        var mgr = Create();
        Assert.IsType<ChannelJoined>(mgr.JoinChannel(251000, "client-1", "Viper", maxClientsPerChannel: 1));
        Assert.IsType<ChannelFull>(mgr.JoinChannel(251000, "client-2", "Maverick", maxClientsPerChannel: 1));

        Assert.Equal(["client-1"], mgr.GetClientsInChannel(251000));
    }

    [Fact]
    public void JoinChannel_RejectedByCapacity_DoesNotStrandAnEmptyChannel()
    {
        var mgr = Create();

        Assert.IsType<ChannelFull>(mgr.JoinChannel(251000, "client-1", "Viper", maxClientsPerChannel: 0));

        Assert.Empty(mgr.GetChannelSummaries().Select(s => s.FrequencyKhz));
        Assert.Empty(mgr.GetClientsInChannel(251000));
    }

    [Fact]
    public void JoinChannel_ExistingMemberOnFullChannel_IsNotRefused()
    {
        // A member is already counted in the channel total, so gating the rejoin on capacity
        // would refuse it and strand them off comms. Membership must win over capacity.
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper", maxClientsPerChannel: 1);

        Assert.IsType<AlreadyInChannel>(mgr.JoinChannel(251000, "client-1", "Viper", maxClientsPerChannel: 1));
        Assert.Contains("client-1", mgr.GetClientsInChannel(251000));
    }

    [Fact]
    public void JoinChannel_CapacityIsPerFrequency()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper", maxClientsPerChannel: 1);

        Assert.IsType<ChannelFull>(mgr.JoinChannel(251000, "client-2", "Maverick", maxClientsPerChannel: 1));
        Assert.IsType<ChannelJoined>(mgr.JoinChannel(135100, "client-2", "Maverick", maxClientsPerChannel: 1));
    }

    [Fact]
    public void JoinChannel_AfterFullChannelDrains_AdmitsAgain()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper", maxClientsPerChannel: 1);
        Assert.IsType<ChannelFull>(mgr.JoinChannel(251000, "client-2", "Maverick", maxClientsPerChannel: 1));

        mgr.LeaveChannel(251000, "client-1");

        Assert.IsType<ChannelJoined>(mgr.JoinChannel(251000, "client-2", "Maverick", maxClientsPerChannel: 1));
    }

    [Fact]
    public void JoinChannel_TwoClientsOnSameFreq_BothSucceed()
    {
        var mgr = Create();
        Assert.IsType<ChannelJoined>(mgr.JoinChannel(251000, "client-1", "Viper"));
        Assert.IsType<ChannelJoined>(mgr.JoinChannel(251000, "client-2", "Maverick"));
    }

    [Fact]
    public void JoinChannel_SameClientDifferentFreqs_BothSucceed()
    {
        var mgr = Create();
        Assert.IsType<ChannelJoined>(mgr.JoinChannel(251000, "client-1", "Viper"));
        Assert.IsType<ChannelJoined>(mgr.JoinChannel(135100, "client-1", "Viper"));
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

        Assert.Empty(mgr.GetClientsInChannel(251000));
    }

    [Fact]
    public void LeaveChannel_OneOfTwo_OtherRemainsInChannel()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");
        mgr.JoinChannel(251000, "client-2", "Maverick");

        mgr.LeaveChannel(251000, "client-1");

        Assert.Single(mgr.GetClientsInChannel(251000));
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
    public void ChannelState_ReturnsCorrectPeerData()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");

        var peers = PeersIn(mgr, 251000);

        Assert.Single(peers);
        Assert.Equal("client-1", peers[0].Id);
        Assert.Equal("Viper", peers[0].Name);
        Assert.Equal(PeerData.PeerStatus.Receiving, peers[0].Status);
    }

    [Fact]
    public void ChannelState_With3dFlag_ReflectedInPeerData()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper", is3d: true);

        var peers = PeersIn(mgr, 251000);

        Assert.True(peers[0].Is3d);
    }

    [Fact]
    public void ChannelState_NonExistentFreq_IsAbsent()
    {
        var mgr = Create();
        Assert.Empty(PeersIn(mgr, 999999));
    }

    [Fact]
    public void GetChannelSummaries_NoChannels_IsEmpty()
    {
        Assert.Empty(Create().GetChannelSummaries());
    }

    [Fact]
    public void GetChannelSummaries_ReportsCountAndTransmitStatePerChannel()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");
        mgr.JoinChannel(251000, "client-2", "Maverick");
        mgr.JoinChannel(135100, "client-3", "Iceman");
        Transmit(mgr, 251000, "client-2", true);

        var summaries = mgr.GetChannelSummaries();

        Assert.Equal([135100, 251000], summaries.Select(s => s.FrequencyKhz));
        Assert.Equal(new ChannelSummary(135100, 1, false), summaries[0]);
        Assert.Equal(new ChannelSummary(251000, 2, true), summaries[1]);
    }

    [Fact]
    public void GetChannelSummaries_TransmitterLeaves_ChannelReportsQuiet()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");
        mgr.JoinChannel(251000, "client-2", "Maverick");
        Transmit(mgr, 251000, "client-2", true);

        mgr.LeaveChannel(251000, "client-2");

        Assert.Equal(new ChannelSummary(251000, 1, false), Assert.Single(mgr.GetChannelSummaries()));
    }

    [Fact]
    public void GetClientChannels_ClientOnMultipleFreqs_ReturnsAll()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");
        mgr.JoinChannel(135100, "client-1", "Viper");

        var channels = mgr.GetClientChannels("client-1");

        Assert.Equal(2, channels.Length);
        Assert.Contains(251000, channels);
        Assert.Contains(135100, channels);
    }

    [Fact]
    public void GetClientChannels_ClientNotJoined_ReturnsEmpty()
    {
        var mgr = Create();
        Assert.Empty(mgr.GetClientChannels("ghost"));
    }

    [Fact]
    public void GetClientChannels_SingleChannel_ReturnsThatFrequency()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");

        Assert.Equal([251000], mgr.GetClientChannels("client-1"));
    }

    [Fact]
    public void IsInAnyChannel_ClientNotJoined_ReturnsFalse()
    {
        var mgr = Create();
        Assert.False(mgr.IsInAnyChannel("ghost"));
    }

    [Fact]
    public void IsInAnyChannel_JoinedThenLeft_TracksMembership()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");
        Assert.True(mgr.IsInAnyChannel("client-1"));

        mgr.LeaveAllChannels("client-1");
        Assert.False(mgr.IsInAnyChannel("client-1"));
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

        var peers = PeersIn(mgr, 251000);
        Assert.Equal("NewName", peers[0].Name);
    }

    [Fact]
    public void UpdateDisplayName_ClientOnMultipleFreqs_NameUpdatedEverywhere()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "OldName");
        mgr.JoinChannel(135100, "client-1", "OldName");

        mgr.UpdateDisplayName("client-1", "NewName");

        Assert.Equal("NewName", PeersIn(mgr, 251000)[0].Name);
        Assert.Equal("NewName", PeersIn(mgr, 135100)[0].Name);
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

        mgr.UpdateIs3d("client-1", true);

        var peers = PeersIn(mgr, 251000);
        Assert.True(peers[0].Is3d);
    }

    [Fact]
    public void UpdateIs3d_AppliesToEveryChannelTheClientIsOn()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper", is3d: false);
        mgr.JoinChannel(135100, "client-1", "Viper", is3d: false);

        mgr.UpdateIs3d("client-1", true);

        Assert.True(PeersIn(mgr, 251000).Single().Is3d);
        Assert.True(PeersIn(mgr, 135100).Single().Is3d);
    }

    [Fact]
    public void UpdateIs3d_LeavesOtherClientsAlone()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper", is3d: false);
        mgr.JoinChannel(251000, "client-2", "Maverick", is3d: false);

        mgr.UpdateIs3d("client-1", true);

        var peers = PeersIn(mgr, 251000);
        Assert.True(peers.Single(p => p.Id == "client-1").Is3d);
        Assert.False(peers.Single(p => p.Id == "client-2").Is3d);
    }

    [Fact]
    public void UpdateIs3d_ClientNotInChannel_NoException()
    {
        var mgr = Create();
        mgr.UpdateIs3d("ghost", true); // should not throw
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

        Assert.Equal(PeerData.PeerStatus.Receiving, PeersIn(mgr, 251000)[0].Status);
    }

    [Fact]
    public void UpdateIs3d_PreservesName()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper", is3d: false);

        mgr.UpdateIs3d("client-1", true);

        Assert.Equal("Viper", PeersIn(mgr, 251000)[0].Name);
    }

    [Fact]
    public void UpdateIs3d_PreservesStatus()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper", is3d: false);

        mgr.UpdateIs3d("client-1", true);

        Assert.Equal(PeerData.PeerStatus.Receiving, PeersIn(mgr, 251000)[0].Status);
    }

    [Fact]
    public void JoinChannel_AfterLeave_CanRejoin()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");
        mgr.LeaveChannel(251000, "client-1");

        Assert.IsType<ChannelJoined>(mgr.JoinChannel(251000, "client-1", "Viper"));
    }

    [Fact]
    public void GetClientChannels_MultipleChannels_ReturnsAllOfThem()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");
        mgr.JoinChannel(135100, "client-1", "Viper");

        Assert.Equal([135100, 251000], mgr.GetClientChannels("client-1").Order());
    }

    [Fact]
    public void LeaveChannel_AfterLeaveAllChannels_ReturnsFalse()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");
        mgr.LeaveAllChannels("client-1");

        Assert.False(mgr.LeaveChannel(251000, "client-1"));
    }

    // --- Concurrency regression tests ---------------------------------------
    //
    // A leave that emptied a channel used to unpublish the whole channel dictionary
    // without rechecking, so a join racing it could be dropped on the floor: the peer
    // landed in a dictionary the leaver then removed from _channels. The client
    // believed it was tuned while the server had no route to it, in either direction.

    [Fact]
    public async Task JoinChannel_RacingLeaveThatEmptiesChannel_JoinerIsNeverLost()
    {
        const int frequencyKhz = 251000;
        const int iterations = 20_000;

        var mgr = Create();
        var joinerLost = 0;

        for (var i = 0; i < iterations; i++)
        {
            // "leaver" is the sole occupant, so its departure empties the channel and
            // takes the cleanup path that used to race.
            mgr.JoinChannel(frequencyKhz, "leaver", "Leaver");

            var joiner = $"joiner-{i}";
            using var gate = new Barrier(2);

            var leaveTask = Task.Run(() =>
            {
                gate.SignalAndWait();
                mgr.LeaveChannel(frequencyKhz, "leaver");
            });

            var joinTask = Task.Run(() =>
            {
                gate.SignalAndWait();
                mgr.JoinChannel(frequencyKhz, joiner, "Joiner");
            });

            await Task.WhenAll(leaveTask, joinTask);

            // The join reported success, so the server must be able to route to it.
            if (!mgr.GetClientsInChannel(frequencyKhz).Contains(joiner))
                joinerLost++;

            mgr.LeaveAllChannels(joiner);
            mgr.LeaveAllChannels("leaver");
        }

        Assert.Equal(0, joinerLost);
    }

    [Fact]
    public void ConcurrentJoinLeave_ManyClients_StateStaysConsistent()
    {
        const int frequencyKhz = 251000;
        const int clients = 16;
        const int iterations = 2_000;

        var mgr = Create();

        Parallel.For(0, clients, c =>
        {
            var clientId = $"client-{c}";
            for (var i = 0; i < iterations; i++)
            {
                mgr.JoinChannel(frequencyKhz, clientId, "Viper");
                mgr.LeaveChannel(frequencyKhz, clientId);
            }
        });

        // Everyone left, so the channel is gone rather than lingering empty.
        Assert.Empty(mgr.GetClientsInChannel(frequencyKhz));
        Assert.Empty(PeersIn(mgr, frequencyKhz));

        // And the manager is still usable afterwards.
        Assert.IsType<ChannelJoined>(mgr.JoinChannel(frequencyKhz, "late-joiner", "Maverick"));
        Assert.Equal(["late-joiner"], mgr.GetClientsInChannel(frequencyKhz));
    }

    [Fact]
    public async Task ConcurrentReadersAndWriters_DoNotDeadlock()
    {
        const int frequencyKhz = 251000;

        var mgr = Create();
        var stop = false;

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                mgr.GetAllChannelStates();
                mgr.GetClientsInChannel(frequencyKhz);
                mgr.GetClientChannels("client-0");
                PeersIn(mgr, frequencyKhz);
            }
        })).ToArray();

        var writers = Enumerable.Range(0, 4).Select(c => Task.Run(() =>
        {
            var clientId = $"client-{c}";
            for (var i = 0; i < 5_000; i++)
            {
                mgr.JoinChannel(frequencyKhz, clientId, "Viper");
                mgr.UpdateDisplayName(clientId, $"Viper-{i}");
                mgr.UpdateIs3d(clientId, i % 2 == 0);
                mgr.LeaveAllChannels(clientId);
            }
        })).ToArray();

        // WaitAsync throws TimeoutException rather than hanging the suite if the
        // lock discipline ever regresses into a deadlock.
        await Task.WhenAll(writers).WaitAsync(TimeSpan.FromSeconds(60));
        Volatile.Write(ref stop, true);
        await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(60));
    }

    // --- Transmit status ------------------------------------------------------

    [Fact]
    public void JoinChannel_NewPeer_StartsReceiving()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");

        Assert.Equal(PeerData.PeerStatus.Receiving, PeersIn(mgr, 251000).Single().Status);
    }

    [Fact]
    public void SetTransmissionState_IsVisibleInChannelState()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");

        Assert.NotNull(Transmit(mgr, 251000, "client-1", true));
        Assert.Equal(PeerData.PeerStatus.Transmitting, mgr.GetAllChannelStates()[251000].Single().Status);

        Assert.NotNull(Transmit(mgr, 251000, "client-1", false));
        Assert.Equal(PeerData.PeerStatus.Receiving, mgr.GetAllChannelStates()[251000].Single().Status);
    }

    [Fact]
    public void SetTransmissionState_ClientNotInChannel_ReturnsNull()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");

        Assert.Null(Transmit(mgr, 251000, "ghost", true));
        Assert.Null(Transmit(mgr, 135100, "client-1", true));
    }

    [Fact]
    public void SetTransmissionState_SurvivesDisplayNameAndModeUpdates()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");
        Transmit(mgr, 251000, "client-1", true);

        mgr.UpdateDisplayName("client-1", "Viper 1-1");
        mgr.UpdateIs3d("client-1", true);

        var peer = PeersIn(mgr, 251000).Single();
        Assert.Equal(PeerData.PeerStatus.Transmitting, peer.Status);
        Assert.Equal("Viper 1-1", peer.Name);
        Assert.True(peer.Is3d);
    }

    [Fact]
    public void SetTransmissionState_IsPerFrequency()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");
        mgr.JoinChannel(135100, "client-1", "Viper");

        Transmit(mgr, 251000, "client-1", true);

        Assert.Contains(PeersIn(mgr, 251000), p => p.Status == PeerData.PeerStatus.Transmitting);
        Assert.DoesNotContain(PeersIn(mgr, 135100), p => p.Status == PeerData.PeerStatus.Transmitting);
        Assert.Equal(1, mgr.CountTransmitting());
    }

    [Fact]
    public void CountTransmitting_CountsEachClientFrequencyPair()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");
        mgr.JoinChannel(135100, "client-1", "Viper");
        mgr.JoinChannel(251000, "client-2", "Maverick");

        Assert.Equal(0, mgr.CountTransmitting());

        Transmit(mgr, 251000, "client-1", true);
        Transmit(mgr, 135100, "client-1", true);
        Transmit(mgr, 251000, "client-2", true);

        Assert.Equal(3, mgr.CountTransmitting());
    }

    [Fact]
    public void LeaveChannel_ClearsTransmittingState()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");
        Transmit(mgr, 251000, "client-1", true);

        mgr.LeaveChannel(251000, "client-1");

        Assert.Equal(0, mgr.CountTransmitting());
        Assert.DoesNotContain(PeersIn(mgr, 251000), p => p.Status == PeerData.PeerStatus.Transmitting);
    }

    [Fact]
    public void Rejoin_AfterTransmitting_StartsReceivingAgain()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");
        Transmit(mgr, 251000, "client-1", true);
        mgr.LeaveChannel(251000, "client-1");

        mgr.JoinChannel(251000, "client-1", "Viper");

        Assert.Equal(PeerData.PeerStatus.Receiving, PeersIn(mgr, 251000).Single().Status);
    }

    // --- Per-client and per-server views --------------------------------------

    [Fact]
    public void GetClientChannelStates_ReturnsFrequencyAndPeer()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");
        mgr.JoinChannel(135100, "client-1", "Viper");
        mgr.JoinChannel(243000, "client-2", "Maverick");
        Transmit(mgr, 251000, "client-1", true);

        var states = mgr.GetClientChannelStates("client-1").OrderBy(s => s.FrequencyKhz).ToList();

        Assert.Equal([135100, 251000], states.Select(s => s.FrequencyKhz));
        Assert.Equal(PeerData.PeerStatus.Receiving, states[0].Peer.Status);
        Assert.Equal(PeerData.PeerStatus.Transmitting, states[1].Peer.Status);
    }

    [Fact]
    public void GetClientChannelStates_ClientNotJoined_ReturnsEmpty()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");

        Assert.Empty(mgr.GetClientChannelStates("ghost"));
    }

    [Fact]
    public void GetChannelSummaries_ReturnsOnlyOccupiedChannels()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "client-1", "Viper");
        mgr.JoinChannel(135100, "client-2", "Maverick");

        Assert.Equal([135100, 251000], mgr.GetChannelSummaries().Select(s => s.FrequencyKhz));

        mgr.LeaveAllChannels("client-2");
        Assert.Equal([251000], mgr.GetChannelSummaries().Select(s => s.FrequencyKhz));
    }

    // --- Relay resolution -----------------------------------------------------

    [Fact]
    public void ResolveRelay_ReturnsPeersExcludingSender()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "sender", "Viper");
        mgr.JoinChannel(251000, "listener-1", "Maverick");
        mgr.JoinChannel(251000, "listener-2", "Goose");

        var targets = mgr.ResolveRelay("sender", [251000]);

        Assert.Equal([251000], targets.Valid);
        Assert.Empty(targets.Rejected);
        Assert.Equal(["listener-1", "listener-2"], targets.Recipients.Order());
    }

    [Fact]
    public void ResolveRelay_UnjoinedFrequency_IsRejectedAndRoutesNowhere()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "sender", "Viper");
        mgr.JoinChannel(135100, "listener", "Maverick");

        var targets = mgr.ResolveRelay("sender", [135100]);

        Assert.Empty(targets.Valid);
        Assert.Equal([135100], targets.Rejected);
        Assert.Empty(targets.Recipients);
    }

    [Fact]
    public void ResolveRelay_MixedFrequencies_SplitsValidFromRejected()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "sender", "Viper");
        mgr.JoinChannel(251000, "listener", "Maverick");

        var targets = mgr.ResolveRelay("sender", [251000, 135100]);

        Assert.Equal([251000], targets.Valid);
        Assert.Equal([135100], targets.Rejected);
        Assert.Equal(["listener"], targets.Recipients);
    }

    [Fact]
    public void ResolveRelay_ListenerOnTwoMatchingFrequencies_IsListedOnce()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "sender", "Viper");
        mgr.JoinChannel(135100, "sender", "Viper");
        mgr.JoinChannel(251000, "listener", "Maverick");
        mgr.JoinChannel(135100, "listener", "Maverick");

        var targets = mgr.ResolveRelay("sender", [251000, 135100]);

        Assert.Equal(["listener"], targets.Recipients);
    }

    [Fact]
    public void ResolveRelay_SenderAloneOnChannel_IsValidButRoutesNowhere()
    {
        var mgr = Create();
        mgr.JoinChannel(251000, "sender", "Viper");

        var targets = mgr.ResolveRelay("sender", [251000]);

        Assert.Equal([251000], targets.Valid);
        Assert.Empty(targets.Recipients);
    }
}
