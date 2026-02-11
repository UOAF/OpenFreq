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
    
    Mode OwnPositionMode { get; }

    // Events
    event EventHandler<ConnectionState>? ConnectionStateChanged;
    event EventHandler<string>? StatusMessageReceived;
    event EventHandler<FrequencyStatusEventArgs>? FrequencyStatusChanged;
    event EventHandler<PeerActivityEventArgs>? PeerActivityReceived;

    // Methods
    Task Initialize(OpenFreqClient.Models.OpenFreqSettings settings, int recordingDeviceIndex, int playbackDeviceIndex);
    Task ConnectAsync();
    Task DisconnectAsync();
    bool FrequencyJoined(int frequencyKhz);
    Task JoinFrequencyAsync(int frequencyKhz, RadioStationData radioStationData, bool isEnabled);
    Task LeaveFrequencyAsync(int frequencyKhz);
    Task StartTransmissionAsync(int frequencyKhz, List<int> mutedFrequencies);
    Task StopTransmissionAsync(int frequencyKhz);

    void SetVolume(int frequencyKhz, float volumeValue);
    void SetAudioChannel(int frequencyKhz, RadioPlayback.AudioChannel channel);
    
    void EnableFrequency(int frequencyKhz);
    void DisableFrequency(int frequencyKhz);
    
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