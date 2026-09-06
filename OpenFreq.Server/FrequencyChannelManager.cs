using OpenFreq.Common;

namespace OpenFreqServer;

/// <summary>
/// The outcome of resolving one inbound audio packet against channel membership.
/// </summary>
/// <param name="Valid">Requested frequencies the sender is actually joined to.</param>
/// <param name="Rejected">Requested frequencies the sender is not joined to.</param>
/// <param name="Recipients">Deduplicated clients to relay to, excluding the sender.</param>
public readonly record struct RelayTargets(int[] Valid, int[] Rejected, string[] Recipients);

// Can a brother get some sum types/tagged unions?
public abstract record JoinChannelResult;
public record ChannelJoined : JoinChannelResult;
public record AlreadyInChannel : JoinChannelResult;
public record ChannelFull : JoinChannelResult;

/// <summary>One frequency's headline state, for the server's own status views.</summary>
public readonly record struct ChannelSummary(int FrequencyKhz, int ClientCount, bool IsTransmitting);

/// <summary>
/// Manages frequency channels and tracks peer state for broadcasting
/// </summary>
public class FrequencyChannelManager
{
    private readonly Dictionary<int, Dictionary<string, PeerData>> _channels = new();

    /// <summary>
    /// Join a channel with initial peer data, admitting the client only if the channel has
    /// room for them.
    /// </summary>
    public JoinChannelResult JoinChannel(
        int frequencyKhz,
        string clientId,
        string displayName,
        bool is3d = false,
        int maxClientsPerChannel = int.MaxValue)
    {
        lock (_channels)
        {
            if (!_channels.TryGetValue(frequencyKhz, out var peers))
            {
                peers = [];
                _channels[frequencyKhz] = peers;
            }

            if (peers.ContainsKey(clientId)) return new AlreadyInChannel();

            if (peers.Count >= maxClientsPerChannel)
            {
                // Don't strand a channel we created above just to reject the join.
                if (peers.Count == 0) _channels.Remove(frequencyKhz);
                return new ChannelFull();
            }

            peers[clientId] = new PeerData(clientId, displayName, PeerData.PeerStatus.Receiving, is3d);
            return new ChannelJoined();
        }
    }

    /// <summary>
    /// Leave a specific channel
    /// </summary>
    public bool LeaveChannel(int frequencyKhz, string clientId)
    {
        lock (_channels)
        {
            if (!_channels.TryGetValue(frequencyKhz, out var peers)) return false;
            var removed = peers.Remove(clientId);
            if (peers.Count == 0) _channels.Remove(frequencyKhz);
            return removed;
        }
    }

    /// <summary>
    /// Leave all channels for a client
    /// </summary>
    public void LeaveAllChannels(string clientId)
    {
        lock (_channels)
        {
            // Materialize the keys: the loop removes emptied channels from _channels.
            foreach (var frequency in _channels.Keys.ToList())
            {
                var peers = _channels[frequency];
                if (!peers.Remove(clientId)) continue;
                if (peers.Count == 0) _channels.Remove(frequency);
            }
        }
    }

    /// <summary>
    /// Update the display name for a peer across all channels they're in
    /// </summary>
    public void UpdateDisplayName(string clientId, string newDisplayName)
    {
        lock (_channels)
        {
            foreach (var peers in _channels.Values)
            {
                if (!peers.TryGetValue(clientId, out var current)) continue;

                // Replace rather than mutate: getters hand these instances out, so the
                // snapshots callers are already holding must not change under them.
                peers[clientId] = new PeerData(
                    current.Id,
                    newDisplayName,
                    current.Status,
                    current.Is3d);
            }
        }
    }

    /// <summary>
    /// Update the last-known 3D mode for a peer across every channel they're in.
    /// </summary>
    public void UpdateIs3d(string clientId, bool is3d)
    {
        lock (_channels)
        {
            foreach (var peers in _channels.Values)
            {
                if (!peers.TryGetValue(clientId, out var current)) continue;

                peers[clientId] = new PeerData(current.Id, current.Name, current.Status, is3d);
            }
        }
    }

    /// <summary>
    /// Get all client IDs in a channel (for backward compatibility)
    /// </summary>
    public string[] GetClientsInChannel(int frequencyKhz)
    {
        lock (_channels)
        {
            return _channels.TryGetValue(frequencyKhz, out var peers)
                ? peers.Keys.ToArray()
                : [];
        }
    }

    /// <summary>
    /// Whether a client is routable on any frequency at all.
    /// </summary>
    public bool IsInAnyChannel(string clientId)
    {
        lock (_channels)
        {
            foreach (var peers in _channels.Values)
            {
                if (peers.ContainsKey(clientId)) return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Record a client's transmit state and 3D mode on one frequency, and report the other
    /// clients there — the peers that need to be told about the change.
    /// </summary>
    /// <returns>
    /// The other clients on the frequency, or null if this client is not on it. Empty means
    /// they are talking to nobody, which is not the same as not being tuned.
    /// </returns>
    public string[]? SetTransmissionState(int frequencyKhz, string clientId, bool transmitting, bool is3d)
    {
        lock (_channels)
        {
            if (!_channels.TryGetValue(frequencyKhz, out var peers) ||
                !peers.TryGetValue(clientId, out var current))
            {
                return null;
            }

            peers[clientId] = new PeerData(
                current.Id,
                current.Name,
                transmitting ? PeerData.PeerStatus.Transmitting : PeerData.PeerStatus.Receiving,
                is3d);

            return peers.Keys.Where(id => id != clientId).ToArray();
        }
    }

    /// <summary>
    /// Every frequency a client is on, paired with its peer entry there.
    /// </summary>
    public List<(int FrequencyKhz, PeerData Peer)> GetClientChannelStates(string clientId)
    {
        lock (_channels)
        {
            var result = new List<(int, PeerData)>();

            foreach (var (frequency, peers) in _channels)
            {
                if (peers.TryGetValue(clientId, out var peer)) result.Add((frequency, peer));
            }

            return result;
        }
    }

    /// <summary>
    /// Every active frequency with its client count and whether anyone is talking on it,
    /// ordered by frequency.
    /// </summary>
    public List<ChannelSummary> GetChannelSummaries()
    {
        lock (_channels)
        {
            var summaries = new List<ChannelSummary>(_channels.Count);

            foreach (var (frequency, peers) in _channels)
            {
                var transmitting = false;
                foreach (var peer in peers.Values)
                {
                    if (peer.Status != PeerData.PeerStatus.Transmitting) continue;
                    transmitting = true;
                    break;
                }

                summaries.Add(new ChannelSummary(frequency, peers.Count, transmitting));
            }

            summaries.Sort((a, b) => a.FrequencyKhz.CompareTo(b.FrequencyKhz));
            return summaries;
        }
    }

    /// <summary>
    /// Total number of client/frequency pairs currently transmitting. A client talking on
    /// two frequencies counts twice.
    /// </summary>
    public int CountTransmitting()
    {
        lock (_channels)
        {
            var count = 0;

            foreach (var peers in _channels.Values)
            {
                foreach (var peer in peers.Values)
                {
                    if (peer.Status == PeerData.PeerStatus.Transmitting) count++;
                }
            }

            return count;
        }
    }

    /// <summary>
    /// Resolves one inbound audio packet against channel membership:
    /// which of the requested frequencies the sender may actually transmit on, which it
    /// may not, and the deduplicated set of clients that should receive the audio.
    /// </summary>
    public RelayTargets ResolveRelay(string senderClientId, IReadOnlyList<int> requestedKhz)
    {
        lock (_channels)
        {
            List<int> valid = [];
            List<int> rejected = [];
            HashSet<string> recipients = [];

            foreach (var frequencyKhz in requestedKhz)
            {
                if (!_channels.TryGetValue(frequencyKhz, out var peers) ||
                    !peers.ContainsKey(senderClientId))
                {
                    rejected.Add(frequencyKhz);
                    continue;
                }

                valid.Add(frequencyKhz);

                foreach (var clientId in peers.Keys)
                {
                    if (clientId != senderClientId) recipients.Add(clientId);
                }
            }

            return new RelayTargets([.. valid], [.. rejected], [.. recipients]);
        }
    }

    /// <summary>
    /// Get the complete channel state across all frequencies
    /// </summary>
    public SortedDictionary<int, List<PeerData>> GetAllChannelStates()
    {
        lock (_channels)
        {
            var result = new SortedDictionary<int, List<PeerData>>();

            foreach (var (frequency, peers) in _channels)
            {
                result[frequency] = peers.Values
                    .OrderBy(p => p.Name)
                    .ToList();
            }

            return result;
        }
    }

    /// <summary>
    /// Get all channels a client is in
    /// </summary>
    public int[] GetClientChannels(string clientId)
    {
        lock (_channels)
        {
            List<int> channels = [];

            foreach (var (frequency, peers) in _channels)
            {
                if (peers.ContainsKey(clientId))
                {
                    channels.Add(frequency);
                }
            }

            return [.. channels];
        }
    }

}
