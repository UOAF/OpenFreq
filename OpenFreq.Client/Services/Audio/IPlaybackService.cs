using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OpenFreqAudio;

namespace OpenFreqClient.Services.Audio;

/// <summary>
/// Abstraction over <see cref="RadioPlayback"/> covering the surface used by
/// <see cref="OpenFreqClient.Services.OpenFreqService"/>. Exists so the BASS-backed playback engine
/// (which needs a real audio device) can be replaced with a fake in tests.
/// </summary>
public interface IPlaybackService
{
    event Action<string>? UserFacingError;

    bool SidetoneEnabled { get; set; }
    float SidetoneVolume { get; set; }
    float MasterVolume { get; set; }
    float AmbientNoiseVolume { get; set; }
    bool Apply3dEffects { get; set; }
    bool OwnVoiceSfxEnabled { get; set; }
    bool IsRecording { get; }
    bool IsMonitoring { get; }
    bool IsCapturing { get; }

    void Initialize();
    void EnsureMasterStreamRunning();
    void ChangeOutputDevice(int newDeviceIndex);
    Task StopAll();

    void TuneFrequency(int frequencyKhz, Guid slotId);
    void UntuneFrequency(int frequencyKhz, Guid slotId);
    void SetSquelchLevel(int frequencyKhz, Guid slotId, float squelchLevel);
    void SetFrequencyVolume(int frequencyKhz, Guid slotId, float volume);
    void SetFrequencyPan(int frequencyKhz, Guid slotId, int pan);

    void AddTransmittingFrequencies(IEnumerable<int> frequencies);
    void RemoveTransmittingFrequencies(IEnumerable<int> frequencies);

    void StartPushStream(string streamId, int sampleRate, int channels, AudioParams audioParams);
    bool PushAudioData(string streamId, Memory<short> audioData, AmbientNoiseType ambientNoise = AmbientNoiseType.None);
    void UpdateStreamParams(string streamId, AudioParams newParams);
    Task StopStream(string streamId);
    List<string> GetActiveStreams();
    bool IsStreamActive(string id);

    void StartRecording(string filePath);
    void StopRecording();
    void StartMonitor(int deviceIndex);
    void StopMonitor();
    void ClearSidetone();

    void PushSidetone(ReadOnlySpan<float> samples);
    void PushOwnVoiceForRecording(ReadOnlySpan<float> samples);
    void SetOwnVoiceRecordParams(AudioParams p, AmbientNoiseType ambient);
}

/// <summary>Adapter forwarding <see cref="IPlaybackService"/> to a real <see cref="RadioPlayback"/>.</summary>
public sealed class RadioPlaybackAdapter : IPlaybackService
{
    private readonly RadioPlayback _inner;

    public RadioPlaybackAdapter(RadioPlayback inner)
    {
        _inner = inner;
        _inner.UserFacingError += msg => UserFacingError?.Invoke(msg);
    }

    public event Action<string>? UserFacingError;

    public bool SidetoneEnabled { get => _inner.SidetoneEnabled; set => _inner.SidetoneEnabled = value; }
    public float SidetoneVolume { get => _inner.SidetoneVolume; set => _inner.SidetoneVolume = value; }
    public float MasterVolume { get => _inner.MasterVolume; set => _inner.MasterVolume = value; }
    public float AmbientNoiseVolume { get => _inner.AmbientNoiseVolume; set => _inner.AmbientNoiseVolume = value; }
    public bool Apply3dEffects { get => _inner.Apply3dEffects; set => _inner.Apply3dEffects = value; }
    public bool OwnVoiceSfxEnabled { get => _inner.OwnVoiceSfxEnabled; set => _inner.OwnVoiceSfxEnabled = value; }
    public bool IsRecording => _inner.IsRecording;
    public bool IsMonitoring => _inner.IsMonitoring;
    public bool IsCapturing => _inner.IsCapturing;

    public void Initialize() => _inner.Initialize();
    public void EnsureMasterStreamRunning() => _inner.EnsureMasterStreamRunning();
    public void ChangeOutputDevice(int newDeviceIndex) => _inner.ChangeOutputDevice(newDeviceIndex);
    public Task StopAll() => _inner.StopAll();

    public void TuneFrequency(int frequencyKhz, Guid slotId) => _inner.TuneFrequency(frequencyKhz, slotId);
    public void UntuneFrequency(int frequencyKhz, Guid slotId) => _inner.UntuneFrequency(frequencyKhz, slotId);
    public void SetSquelchLevel(int frequencyKhz, Guid slotId, float squelchLevel)
        => _inner.SetSquelchLevel(frequencyKhz, slotId, squelchLevel);
    public void SetFrequencyVolume(int frequencyKhz, Guid slotId, float volume)
        => _inner.SetFrequencyVolume(frequencyKhz, slotId, volume);
    public void SetFrequencyPan(int frequencyKhz, Guid slotId, int pan)
        => _inner.SetFrequencyPan(frequencyKhz, slotId, pan);

    public void AddTransmittingFrequencies(IEnumerable<int> frequencies)
        => _inner.AddTransmittingFrequencies(frequencies);
    public void RemoveTransmittingFrequencies(IEnumerable<int> frequencies)
        => _inner.RemoveTransmittingFrequencies(frequencies);

    public void StartPushStream(string streamId, int sampleRate, int channels, AudioParams audioParams)
        => _inner.StartPushStream(streamId, sampleRate, channels, audioParams);
    public bool PushAudioData(string streamId, Memory<short> audioData, AmbientNoiseType ambientNoise = AmbientNoiseType.None)
        => _inner.PushAudioData(streamId, audioData, ambientNoise);
    public void UpdateStreamParams(string streamId, AudioParams newParams)
        => _inner.UpdateStreamParams(streamId, newParams);
    public Task StopStream(string streamId) => _inner.StopStream(streamId);
    public List<string> GetActiveStreams() => _inner.GetActiveStreams();
    public bool IsStreamActive(string id) => _inner.IsStreamActive(id);

    public void StartRecording(string filePath) => _inner.StartRecording(filePath);
    public void StopRecording() => _inner.StopRecording();
    public void StartMonitor(int deviceIndex) => _inner.StartMonitor(deviceIndex);
    public void StopMonitor() => _inner.StopMonitor();
    public void ClearSidetone() => _inner.ClearSidetone();

    public void PushSidetone(ReadOnlySpan<float> samples) => _inner.PushSidetone(samples);
    public void PushOwnVoiceForRecording(ReadOnlySpan<float> samples) => _inner.PushOwnVoiceForRecording(samples);
    public void SetOwnVoiceRecordParams(AudioParams p, AmbientNoiseType ambient)
        => _inner.SetOwnVoiceRecordParams(p, ambient);
}

/// <summary>
/// Creates <see cref="IPlaybackService"/> instances. Default implementation constructs the real
/// BASS-backed <see cref="RadioPlayback"/>; tests substitute a fake factory.
/// </summary>
public interface IPlaybackServiceFactory
{
    IPlaybackService Create(Microsoft.Extensions.Logging.ILoggerFactory loggerFactory, int deviceIndex);
}

/// <summary>Default factory producing the real BASS playback service.</summary>
public sealed class RadioPlaybackServiceFactory : IPlaybackServiceFactory
{
    public IPlaybackService Create(Microsoft.Extensions.Logging.ILoggerFactory loggerFactory, int deviceIndex)
        => new RadioPlaybackAdapter(new RadioPlayback(loggerFactory, deviceIndex));
}
