using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using OpenFreq.Common;

namespace OpenFreqClient.ViewModels;

internal sealed record PeerFrequencySnapshot(int FrequencyKhz, List<PeerData> Peers);

/// <summary>
/// Keeps the peer panel's view models, and therefore its Avalonia item containers, stable
/// across full server snapshots. Only changed groups and peers are inserted or removed.
/// </summary>
internal static class PeerListReconciler
{
    public static bool Reconcile(
        ObservableCollection<ChannelFrequencyPeerViewModel> groups,
        IReadOnlyList<PeerFrequencySnapshot> snapshot,
        bool is3dFrequency,
        bool is3dMode,
        bool modeIsGci,
        string? ownPeerId,
        Func<int, bool> isFrequencyAlreadyConnected,
        Action<int> joinFrequency)
    {
        bool membershipChanged = false;

        for (int i = 0; i < snapshot.Count; i++)
        {
            var desired = snapshot[i];
            int existingIndex = FindGroup(groups, desired.FrequencyKhz, i);
            if (existingIndex < 0)
            {
                groups.Insert(i, new ChannelFrequencyPeerViewModel(desired.FrequencyKhz,
                    new ObservableCollection<ChannelPeerViewModel>(), joinFrequency, is3dFrequency, false));
                membershipChanged = true;
            }
            else if (existingIndex != i)
            {
                groups.Move(existingIndex, i);
            }

            var group = groups[i];
            group.CanJoin = modeIsGci && is3dMode == is3dFrequency &&
                            !isFrequencyAlreadyConnected(desired.FrequencyKhz);
            membershipChanged |= ReconcilePeers(group.Peers, desired.Peers,
                is3dMode == is3dFrequency, ownPeerId);
        }

        while (groups.Count > snapshot.Count)
        {
            groups.RemoveAt(groups.Count - 1);
            membershipChanged = true;
        }

        return membershipChanged;
    }

    private static bool ReconcilePeers(
        ObservableCollection<ChannelPeerViewModel> peers,
        IReadOnlyList<PeerData> snapshot,
        bool showTransmitting,
        string? ownPeerId)
    {
        bool membershipChanged = false;

        for (int i = 0; i < snapshot.Count; i++)
        {
            var desired = snapshot[i];
            int existingIndex = FindPeer(peers, desired.Id, i);
            if (existingIndex < 0)
            {
                peers.Insert(i, new ChannelPeerViewModel(desired.Id, desired.Name ?? string.Empty,
                    showTransmitting && desired.Status == PeerData.PeerStatus.Transmitting,
                    desired.Id == ownPeerId));
                membershipChanged = true;
            }
            else if (existingIndex != i)
            {
                peers.Move(existingIndex, i);
            }

            var peer = peers[i];
            peer.Name = desired.Name ?? string.Empty;
            peer.IsOwnUser = desired.Id == ownPeerId;
            peer.IsTransmitting = showTransmitting &&
                                  desired.Status == PeerData.PeerStatus.Transmitting;
        }

        while (peers.Count > snapshot.Count)
        {
            peers.RemoveAt(peers.Count - 1);
            membershipChanged = true;
        }

        return membershipChanged;
    }

    private static int FindGroup(
        ObservableCollection<ChannelFrequencyPeerViewModel> groups, int frequencyKhz, int start)
    {
        for (int i = start; i < groups.Count; i++)
            if (groups[i].FrequencyKhz == frequencyKhz)
                return i;
        return -1;
    }

    private static int FindPeer(ObservableCollection<ChannelPeerViewModel> peers, string id, int start)
    {
        for (int i = start; i < peers.Count; i++)
            if (peers[i].Id == id)
                return i;
        return -1;
    }
}
