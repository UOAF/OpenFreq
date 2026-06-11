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
    bool MicNormalizationEnabled { get; set; }
    double SidetoneVolume { get; set; }
    double MasterVolume { get; set; }
    double AmbientNoiseVolume { get; set; }

    /// <summary>Where the combined capture mix goes when recording.</summary>
    enum CaptureSink
    {
        /// <summary>Write to an Ogg/Vorbis file.</summary>
        File,
        /// <summary>Stream to a separate playback device (e.g. a virtual audio cable).</summary>
        Device
    }

    /// <summary>Selects the capture output: file or playback device.</summary>
    CaptureSink Sink { get; set; }
    /// <summary>BASS device index the capture mix is streamed to when <see cref="Sink"/> is Device.</summary>
    int MonitorDeviceIndex { get; set; }
    /// <summary>When true, capture auto-starts on entering game mode (flight) and auto-stops on leaving it.</summary>
    bool AutoRecordInGameMode { get; set; }
    /// <summary>When true, own voice in the capture gets the full radio FX (AGC/squelch/SFX); when false it stays clean.</summary>
    bool ApplyOwnVoiceSfx { get; set; }
    /// <summary>Directory recordings are written to. Created if missing. Blank → "recordings" next to the executable.</summary>
    string RecordingPath { get; set; }
    /// <summary>True while a capture (file or device) is in progress.</summary>
    bool IsRecording { get; }
    /// <summary>Start a capture now (manual or auto) using the selected sink. No-op if already capturing or not initialized.</summary>
    void StartRecording();
    /// <summary>Stop the current capture now. No-op if idle. Always honoured (manual override).</summary>
    void StopRecording();
    /// <summary>Raised when capture starts (true) or stops (false).</summary>
    event EventHandler<bool>? RecordingStateChanged;

    Mode OwnPositionMode { get; }

    // Events
    event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
    event EventHandler<string>? StatusMessageReceived;
    /// <summary>
    /// Fired when the audio playback subsystem (RadioPlayback/BASS) encounters a user-facing error
    /// such as device switch failure or playback loss. Message is human-readable, no stack trace.
    /// </summary>
    event EventHandler<string>? AudioPlaybackErrorOccurred;
    event EventHandler<FrequencyConnectionStatusEventArgs>? FrequencyConnectionStatusChanged;
    event EventHandler<FrequencyTransmissionStatusEventArgs>? FrequencyTransmissionStatusChanged;

    public event EventHandler<FrequencyJoinedEventArgs>? FrequencyJoined;
    public event EventHandler<PeerEventArgs>? PeerJoined;
    public event EventHandler<PeerEventArgs>? PeerLeft;
    public event EventHandler<AllPeersStatusEventArgs>? AllPeersStatusChanged;
    event EventHandler<PeerActivityEventArgs>? PeerActivityReceived;

    // Methods
    Task Initialize(OpenFreqClient.Models.OpenFreqSettings settings, int recordingDeviceIndex, int playbackDeviceIndex);
    Task ConnectAsync(TimeSpan? connectTimeout = null);
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

    /// <summary>
    /// Samples terrain elevation (meters MSL) at BMS heightmap coordinates. Null if no heightmap loaded.
    /// </summary>
    public double? SampleTerrainElevationMeters(double xMeters, double yMeters);

    public void SetOwnPositionMode(Mode newMode);

    public enum Mode
    {
        GCI,
        BMS
    }
}
