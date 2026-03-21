using System.Collections.Concurrent;
using OpenFreq.Common;

namespace OpenFreqServer;

/// <summary>
/// Manages frequency channels and tracks peer state for broadcasting
/// </summary>
public class FrequencyChannelManager
{
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<string, PeerData>> _channels = new();

    /// <summary>
    /// Join a channel with initial peer data
    /// </summary>
    public bool JoinChannel(int frequencyKhz, string clientId, string displayName)
    {
        var channelPeers = _channels.GetOrAdd(frequencyKhz, _ => new ConcurrentDictionary<string, PeerData>());
        var peerData = new PeerData(clientId, displayName, PeerData.PeerStatus.Receiving);
        return channelPeers.TryAdd(clientId, peerData);
    }

    /// <summary>
    /// Leave a specific channel
    /// </summary>
    public bool LeaveChannel(int frequencyKhz, string clientId)
    {
        if (_channels.TryGetValue(frequencyKhz, out var peers))
        {
            var removed = peers.TryRemove(clientId, out _);
            
            // Clean up empty channels
            if (peers.IsEmpty)
            {
                _channels.TryRemove(frequencyKhz, out _);
            }

            return removed;
        }

        return false;
    }

    /// <summary>
    /// Leave all channels for a client
    /// </summary>
    public void LeaveAllChannels(string clientId)
    {
        // Use ToList() to avoid modification during enumeration
        var frequencies = _channels.Keys.ToList();

        foreach (var frequency in frequencies)
        {
            if (_channels.TryGetValue(frequency, out var peers))
            {
                if (peers.TryRemove(clientId, out _))
                {
                    // Clean up empty channels
                    if (peers.IsEmpty)
                    {
                        _channels.TryRemove(frequency, out _);
                    }
                }
            }
        }
    }
    
    /// <summary>
    /// Update the display name for a peer across all channels they're in
    /// </summary>
    public void UpdateDisplayName(string clientId, string newDisplayName)
    {
        foreach (var (frequency, peers) in _channels)
        {
            if (peers.TryGetValue(clientId, out var currentPeerData))
            {
                var updatedPeerData = new PeerData(
                    currentPeerData.Id, 
                    newDisplayName, 
                    currentPeerData.Status);
                
                peers.TryUpdate(clientId, updatedPeerData, currentPeerData);
            }
        }
    }

    /// <summary>
    /// Get all client IDs in a channel (for backward compatibility)
    /// </summary>
    public string[] GetClientsInChannel(int frequencyKhz)
    {
        if (_channels.TryGetValue(frequencyKhz, out var peers))
        {
            return peers.Keys.ToArray();
        }

        return Array.Empty<string>();
    }

    /// <summary>
    /// Get all peer data in a specific channel
    /// </summary>
    public List<PeerData> GetPeersInChannel(int frequencyKhz)
    {
        if (_channels.TryGetValue(frequencyKhz, out var peers))
        {
            return peers.Values.ToList();
        }

        return new List<PeerData>();
    }

    /// <summary>
    /// Get the complete channel state across all frequencies
    /// </summary>
    public SortedDictionary<int, List<PeerData>> GetAllChannelStates()
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

    /// <summary>
    /// Get first channel for a client (for backward compatibility)
    /// </summary>
    public double GetClientChannel(string clientId)
    {
        foreach (var kvp in _channels)
        {
            if (kvp.Value.ContainsKey(clientId))
            {
                return kvp.Key;
            }
        }

        return -1;
    }

    /// <summary>
    /// Get all channels a client is in
    /// </summary>
    public List<double> GetClientChannels(string clientId)
    {
        var channels = new List<double>();
        
        foreach (var kvp in _channels)
        {
            if (kvp.Value.ContainsKey(clientId))
            {
                channels.Add(kvp.Key);
            }
        }
        
        return channels;
    }

    /// <summary>
    /// Get the number of peers in a channel
    /// </summary>
    public int GetChannelCount(int frequencyKhz)
    {
        if (_channels.TryGetValue(frequencyKhz, out var peers))
        {
            return peers.Count;
        }

        return 0;
    }
}