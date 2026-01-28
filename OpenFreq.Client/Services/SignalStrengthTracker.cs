using System;
using System.Collections.Concurrent;
using System.Threading;
using OpenFreqAudio;

namespace OpenFreqClient.Services;

/// <summary>
/// Tracks signal strength per frequency and periodically notifies of updates.
/// Automatically sets signal to 0 when no transmission is received.
/// </summary>
public class SignalStrengthTracker : IDisposable
{
    private readonly ConcurrentDictionary<int, SignalStrengthData> _signalStrengths = new();
    private readonly Timer _updateTimer;
    private readonly Action<int, float> _onSignalStrengthChanged;

    private class SignalStrengthData
    {
        public float Strength { get; set; }
        public DateTime LastUpdate { get; set; }
    }

    public int UpdateIntervalMs { get; }
    public int SignalTimeoutMs { get; }

    public SignalStrengthTracker(
        Action<int, float> onSignalStrengthChanged,
        int updateIntervalMs = 100,
        int signalTimeoutMs = 500)
    {
        _onSignalStrengthChanged = onSignalStrengthChanged;
        UpdateIntervalMs = updateIntervalMs;
        SignalTimeoutMs = signalTimeoutMs;

        _updateTimer = new Timer(UpdateSignalStrengths, null,
            TimeSpan.FromMilliseconds(updateIntervalMs),
            TimeSpan.FromMilliseconds(updateIntervalMs));
    }

    /// <summary>
    /// Update the signal strength for a given frequency.
    /// Call this from your audio processing pipeline.
    /// </summary>
    public void UpdateSignalStrength(int frequencyKhz, AudioParams audioParams)
    {
        var strength = GetSignalStrength(audioParams);
        _signalStrengths.AddOrUpdate(
            frequencyKhz,
            new SignalStrengthData { Strength = strength, LastUpdate = DateTime.UtcNow },
            (_, existing) =>
            {
                existing.Strength = strength;
                existing.LastUpdate = DateTime.UtcNow;
                return existing;
            });
    }

    private void UpdateSignalStrengths(object? state)
    {
        var now = DateTime.UtcNow;
        var timeout = TimeSpan.FromMilliseconds(SignalTimeoutMs);

        foreach (var kvp in _signalStrengths)
        {
            var frequencyKhz = kvp.Key;
            var data = kvp.Value;

            // Set to 0 if no recent transmission
            var strength = (now - data.LastUpdate) > timeout ? 0f : data.Strength;

            _onSignalStrengthChanged(frequencyKhz, strength);
        }
    }

    public void Dispose()
    {
        _updateTimer?.Dispose();
    }

    private static float GetSignalStrength(AudioParams audioParams)
    {
        // Primary indicator: SNR (Signal-to-Noise Ratio)
        // Typical range: -10 dB (unusable) to +40 dB (extremely strong)
        float snr = audioParams.SNR_dB;

        // Map SNR to 0-100 range
        // -10 dB -> 0%, +40 dB -> 100%
        const float minSnr = -10f;
        const float maxSnr = 40f;
        float strength = ((snr - minSnr) / (maxSnr - minSnr)) * 100f;

        // Apply gain influence (if gain is significantly attenuated)
        // This accounts for cases where gain reduction might indicate weak signals
        if (audioParams.Gain < 0.5f)
        {
            strength *= (0.5f + audioParams.Gain); // Reduce strength for low gain
        }

        // Clamp to 0-100
        return Math.Clamp(strength, 0f, 100f);
    }
    
    public class SignalStrengthUpdateMessage
    {
        public int FrequencyKhz { get; }
        public float Strength { get; } // 0-100
    
        public SignalStrengthUpdateMessage(int frequencyKhz, float strength)
        {
            FrequencyKhz = frequencyKhz;
            Strength = strength;
        }
    }
}