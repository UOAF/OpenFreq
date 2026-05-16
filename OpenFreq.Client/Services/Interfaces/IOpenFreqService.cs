using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OpenFreq.Client.Models;
using OpenFreq.Common;
using OpenFreqAudio;

namespace OpenFreqClient.Services.Interfaces;

/// <summary>
/// Service for managing OpenFreq server connection and audio transmission
/// </summary>
public interface IOpenFreqService : IDisposable
{
    // Connection state
    bool IsConnected { get; }
    bool IsAuthenticated { get; }
    string? PeerId { get; }
    int RecordingDeviceIndex { get; set; }
    int PlaybackDeviceIndex { get; set; }
    int AudioParamsUpdateFrequency { get; set; }
    bool Apply3dAudioEffects { get; set; }
    bool SidetoneEnabled { get; set; }
    double SidetoneVolume { get; set; }
    
    Mode OwnPositionMode { get; }

    // Events
    event EventHandler<ConnectionState>? ConnectionStateChanged;
    event EventHandler<string>? StatusMessageReceived;
    event EventHandler<FrequencyConnectionStatusEventArgs>? FrequencyConnectionStatusChanged;
    event EventHandler<FrequencyTransmissionStatusEventArgs>? FrequencyTransmissionStatusChanged;
    
    public event EventHandler<FrequencyJoinedEventArgs>? FrequencyJoined;
    public event EventHandler<PeerEventArgs>? PeerJoined;
    public event EventHandler<PeerEventArgs>? PeerLeft;
    public event EventHandler<AllPeersStatusEventArgs>? AllPeersStatusChanged;
    event EventHandler<PeerActivityEventArgs>? PeerActivityReceived;

    // Methods
    Task Initialize(OpenFreqClient.Models.OpenFreqSettings settings, int recordingDeviceIndex, int playbackDeviceIndex);
    Task ConnectAsync();
    Task DisconnectAsync();
    bool IsFrequencyJoined(int frequencyKhz, Guid slotId);
    Task JoinFrequencyAsync(int frequencyKhz, Guid slotId, RadioStationData radioStationData);
    Task LeaveFrequencyAsync(int frequencyKhz, Guid slotId);
    Task StartTransmissionAsync(int frequencyKhz, Guid slotId, List<int> mutedFrequencies);
    Task StopTransmissionAsync(int frequencyKhz);
    Task UpdateDisplayNameAsync(string newDisplayName);
    Task NotifyModeAsync(bool is3d);

    void SetVolume(int frequencyKhz, Guid slotId, float volumeValue);
    void SetPan(int frequencyKhz, Guid slotId, int pan);

    void EnableFrequency(int frequencyKhz, Guid slotId);
    void DisableFrequency(int frequencyKhz, Guid slotId);
    
    void SetSquelch(int frequencyKhz, Guid slotId, bool isSquelchClosed);
    
    public enum OpenFreqStatus
    {
        Connected,
        Disconnected,
        Authenticated,
        Connecting
    }

    public OpenFreqStatus Status { get; }
    
    public void LoadHeightmap(string path, int width = 32768, int height = 32768, int bytesPerSample = 2);
    
    public void SetOwnPositionMode(Mode newMode);

    public enum Mode
    {
        GCI,
        BMS
    }
}