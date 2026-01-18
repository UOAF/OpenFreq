using System;
using System.Threading.Tasks;
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
    
    Mode OwnPositionMode { get; }

    // Events
    event EventHandler<ConnectionState>? ConnectionStateChanged;
    event EventHandler<string>? StatusMessageReceived;
    event EventHandler<FrequencyStatusEventArgs>? FrequencyStatusChanged;
    event EventHandler<PeerActivityEventArgs>? PeerActivityReceived;

    // Methods
    void Initialize(OpenFreqClient.Models.OpenFreqSettings settings, int recordingDeviceIndex, int playbackDeviceIndex);
    Task ConnectAsync();
    Task DisconnectAsync();
    Task JoinFrequencyAsync(double frequencyMhz, RadioStationPreset preset);
    Task LeaveFrequencyAsync(double frequencyMhz);
    Task StartTransmissionAsync(double frequencyMhz, RadioStationPreset preset);
    Task StopTransmissionAsync(double frequencyMhz);
    
    
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