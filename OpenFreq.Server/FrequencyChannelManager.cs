using OpenFreq.Common;

namespace OpenFreqServer;

/// <summary>
/// Manages frequency channels and tracks peer state for broadcasting
/// </summary>
public class FrequencyChannelManager
{
    private readonly Dictionary<int, Dictionary<string, PeerData>> _channels = new();

    /// <summary>
    /// Join a channel with initial peer data
    /// </summary>
    public bool JoinChannel(int frequencyKhz, string clientId, string displayName, bool is3d = false)
    {
        lock (_channels)
        {
            if (!_channels.TryGetValue(frequencyKhz, out var peers))
            {
                peers = [];
                _channels[frequencyKhz] = peers;
            }

            // TryAdd, not Add: a duplicate join is an expected outcome that callers read
            // off the return value, not an exception. It can only fail on the pre-existing
            // channel path, so the dictionary created just above is never stranded empty.
            return peers.TryAdd(clientId, new PeerData(clientId, displayName, PeerData.PeerStatus.Receiving, is3d));
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
    /// Update the last-known 3D mode for a peer on a specific frequency
    /// </summary>
    public void UpdateIs3d(int frequencyKhz, string clientId, bool is3d)
    {
        lock (_channels)
        {
            if (_channels.TryGetValue(frequencyKhz, out var peers) &&
                peers.TryGetValue(clientId, out var current))
            {
                peers[clientId] = new PeerData(current.Id, current.Name, current.Status, is3d);
            }
        }
    }

    /// <summary>
    /// Whether a client is currently routable on a frequency.
    /// </summary>
    public bool IsInChannel(int frequencyKhz, string clientId)
    {
        lock (_channels)
        {
            return _channels.TryGetValue(frequencyKhz, out var peers) && peers.ContainsKey(clientId);
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
    /// Get all peer data in a specific channel
    /// </summary>
    public List<PeerData> GetPeersInChannel(int frequencyKhz)
    {
        lock (_channels)
        {
            return _channels.TryGetValue(frequencyKhz, out var peers)
                ? peers.Values.ToList()
                : [];
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
    /// Get first channel for a client (for backward compatibility)
    /// </summary>
    public double GetClientChannel(string clientId)
    {
        lock (_channels)
        {
            foreach (var (frequency, peers) in _channels)
            {
                if (peers.ContainsKey(clientId))
                {
                    return frequency;
                }
            }

            return -1;
        }
    }

    /// <summary>
    /// Get all channels a client is in
    /// </summary>
    public List<double> GetClientChannels(string clientId)
    {
        lock (_channels)
        {
            var channels = new List<double>();

            foreach (var (frequency, peers) in _channels)
            {
                if (peers.ContainsKey(clientId))
                {
                    channels.Add(frequency);
                }
            }

            return channels;
        }
    }

    /// <summary>
    /// Get the number of peers in a channel
    /// </summary>
    public int GetChannelCount(int frequencyKhz)
    {
        lock (_channels)
        {
            return _channels.TryGetValue(frequencyKhz, out var peers) ? peers.Count : 0;
        }
    }
}
