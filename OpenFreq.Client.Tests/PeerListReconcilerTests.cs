using System.Collections.ObjectModel;
using System.Collections.Specialized;
using OpenFreq.Common;
using OpenFreqClient.ViewModels;

namespace OpenFreq.Client.Tests;

public class PeerListReconcilerTests
{
    [Fact]
    public void RepeatedSnapshot_ReusesGroupsAndRowsWithoutCollectionChanges()
    {
        ObservableCollection<ChannelFrequencyPeerViewModel> groups = [];
        Apply(groups, [Snapshot(251_000, Peer("a", "Alpha"), Peer("b", "Bravo"))]);
        var group = groups[0];
        var firstPeer = group.Peers[0];
        var secondPeer = group.Peers[1];
        int groupChanges = 0;
        int peerChanges = 0;
        groups.CollectionChanged += (_, _) => groupChanges++;
        group.Peers.CollectionChanged += (_, _) => peerChanges++;

        for (int i = 0; i < 100; i++)
        {
            bool membershipChanged = Apply(groups,
                [Snapshot(251_000, Peer("a", "Alpha"), Peer("b", "Bravo"))]);
            Assert.False(membershipChanged);
        }

        Assert.Same(group, groups[0]);
        Assert.Same(firstPeer, group.Peers[0]);
        Assert.Same(secondPeer, group.Peers[1]);
        Assert.Equal(0, groupChanges);
        Assert.Equal(0, peerChanges);
    }

    [Fact]
    public void ChangedSnapshot_UpdatesPropertiesAndOnlyChangesAffectedRows()
    {
        ObservableCollection<ChannelFrequencyPeerViewModel> groups = [];
        Apply(groups, [Snapshot(251_000, Peer("a", "Alpha"), Peer("b", "Bravo"))]);
        var originalGroup = groups[0];
        var retainedPeer = originalGroup.Peers[1];
        List<string?> propertyChanges = [];
        retainedPeer.PropertyChanged += (_, e) => propertyChanges.Add(e.PropertyName);

        bool membershipChanged = Apply(groups,
        [
            Snapshot(251_000,
                Peer("b", "Bravo Two", PeerData.PeerStatus.Transmitting),
                Peer("d", "Delta"))
        ], ownPeerId: "b", is3dMode: true, modeIsGci: true);

        Assert.True(membershipChanged);
        Assert.Same(originalGroup, groups[0]);
        Assert.Same(retainedPeer, groups[0].Peers[0]);
        Assert.Equal("Bravo Two", retainedPeer.Name);
        Assert.True(retainedPeer.IsTransmitting);
        Assert.True(retainedPeer.IsOwnUser);
        Assert.Contains(nameof(ChannelPeerViewModel.PeerFontWeight), propertyChanges);
        Assert.Equal(["b", "d"], groups[0].Peers.Select(p => p.Id));
        Assert.True(groups[0].CanJoin);

        Apply(groups,
        [
            Snapshot(250_000, Peer("c", "Charlie")),
            Snapshot(251_000,
                Peer("b", "Bravo Two", PeerData.PeerStatus.Transmitting),
                Peer("d", "Delta"))
        ], ownPeerId: "b", is3dMode: true, modeIsGci: true);

        Assert.Same(originalGroup, groups[1]);
        Assert.Same(retainedPeer, groups[1].Peers[0]);
        Assert.Equal(["b", "d"], groups[1].Peers.Select(p => p.Id));
        Assert.True(groups[1].CanJoin);
    }

    // Avalonia rebuilds the controls of a moved item, so a removal must not move the items after it.
    [Fact]
    public void Removal_DoesNotMoveLaterGroupsOrPeers()
    {
        ObservableCollection<ChannelFrequencyPeerViewModel> groups = [];
        Apply(groups,
        [
            Snapshot(250_000, Peer("a", "Alpha")),
            Snapshot(251_000, Peer("b", "Bravo"), Peer("c", "Charlie"), Peer("d", "Delta"))
        ]);
        var retainedGroup = groups[1];
        List<NotifyCollectionChangedAction> groupActions = [];
        List<NotifyCollectionChangedAction> peerActions = [];
        groups.CollectionChanged += (_, e) => groupActions.Add(e.Action);
        retainedGroup.Peers.CollectionChanged += (_, e) => peerActions.Add(e.Action);

        bool membershipChanged = Apply(groups,
            [Snapshot(251_000, Peer("c", "Charlie"), Peer("d", "Delta"))]);

        Assert.True(membershipChanged);
        Assert.Same(retainedGroup, Assert.Single(groups));
        Assert.Equal(["c", "d"], retainedGroup.Peers.Select(p => p.Id));
        Assert.Equal([NotifyCollectionChangedAction.Remove], groupActions);
        Assert.Equal([NotifyCollectionChangedAction.Remove], peerActions);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void BmsMode_NeverOffersJoinEvenAfterGciSnapshot(bool is3dFrequency, bool is3dMode)
    {
        ObservableCollection<ChannelFrequencyPeerViewModel> groups = [];
        PeerFrequencySnapshot[] snapshot = [Snapshot(251_000, Peer("a", "Alpha"))];

        PeerListReconciler.Reconcile(groups, snapshot, is3dFrequency, is3dMode,
            modeIsGci: true, ownPeerId: null, _ => false, _ => { });
        var originalGroup = groups[0];
        Assert.Equal(is3dFrequency == is3dMode, originalGroup.CanJoin);

        PeerListReconciler.Reconcile(groups, snapshot, is3dFrequency, is3dMode,
            modeIsGci: false, ownPeerId: null, _ => false, _ => { });

        Assert.Same(originalGroup, groups[0]);
        Assert.False(groups[0].CanJoin);
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, true, false)]
    public void GciMode_OnlyOffersJoinForCurrentModeAndDisconnectedFrequency(
        bool is3dFrequency, bool is3dMode, bool isConnected, bool expectedCanJoin)
    {
        ObservableCollection<ChannelFrequencyPeerViewModel> groups = [];

        PeerListReconciler.Reconcile(groups, [Snapshot(251_000, Peer("a", "Alpha"))],
            is3dFrequency, is3dMode, modeIsGci: true, ownPeerId: null,
            _ => isConnected, _ => { });

        Assert.Equal(expectedCanJoin, groups[0].CanJoin);
    }

    private static bool Apply(
        ObservableCollection<ChannelFrequencyPeerViewModel> groups,
        IReadOnlyList<PeerFrequencySnapshot> snapshot,
        string? ownPeerId = null,
        bool is3dMode = false,
        bool modeIsGci = false)
        => PeerListReconciler.Reconcile(groups, snapshot, is3dFrequency: true,
            is3dMode, modeIsGci, ownPeerId, _ => false, _ => { });

    private static PeerFrequencySnapshot Snapshot(int frequencyKhz, params PeerData[] peers)
        => new(frequencyKhz, [.. peers]);

    private static PeerData Peer(
        string id, string name, PeerData.PeerStatus status = PeerData.PeerStatus.Receiving)
        => new(id, name, status, is3d: true);
}
