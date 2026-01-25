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
    private readonly ConcurrentDictionary<double, SignalStrengthData> _signalStrengths = new();
    private readonly Timer _updateTimer;
    private readonly Action<double, float> _onSignalStrengthChanged;

    private class SignalStrengthData
    {
        public float Strength { get; set; }
        public DateTime LastUpdate { get; set; }
    }

    public int UpdateIntervalMs { get; }
    public int SignalTimeoutMs { get; }

    public SignalStrengthTracker(
        Action<double, float> onSignalStrengthChanged,
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
    public void UpdateSignalStrength(double frequencyMhz, AudioParams audioParams)
    {
        var strength = GetSignalStrength(audioParams);
        _signalStrengths.AddOrUpdate(
            frequencyMhz,
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
            var frequencyMhz = kvp.Key;
            var data = kvp.Value;

            // Set to 0 if no recent transmission
            var strength = (now - data.LastUpdate) > timeout ? 0f : data.Strength;

            _onSignalStrengthChanged(frequencyMhz, strength);
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
        public double FrequencyMhz { get; }
        public float Strength { get; } // 0-100
    
        public SignalStrengthUpdateMessage(double frequencyMhz, float strength)
        {
            FrequencyMhz = frequencyMhz;
            Strength = strength;
        }
    }
}