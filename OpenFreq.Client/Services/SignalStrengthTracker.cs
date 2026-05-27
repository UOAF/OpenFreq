using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using OpenFreqAudio;

namespace OpenFreqClient.Services;

/// <summary>
/// Tracks signal strength per frequency and periodically notifies of updates.
/// Displays SNR (Signal-to-Noise Ratio) in dB, which is receiver-independent.
/// </summary>
public class SignalStrengthTracker : IDisposable
{
    private readonly ConcurrentDictionary<int, SignalStrengthData> _signalStrengths = new();
    private readonly ConcurrentDictionary<int, bool> _squelchStates = new();
    private readonly Timer _updateTimer;
    private readonly Timer _noiseTimer;
    private readonly Action<int, (float StrengthPercent, float SnrDb)> _onSignalStrengthChanged;
    private readonly Random _random = new();
    private float _noisePhase;

    private class SignalStrengthData
    {
        public float SnrDb { get; set; }
        public float Strength { get; set; }
        public DateTime LastUpdate { get; set; }
    }

    public int UpdateIntervalMs { get; }
    public int SignalTimeoutMs { get; }

    public SignalStrengthTracker(
        Action<int, (float StrengthPercent, float SnrDb)> onSignalStrengthChanged,
        int updateIntervalMs = 200,
        int signalTimeoutMs = 200,
        int noiseUpdateIntervalMs = 200)
    {
        _onSignalStrengthChanged = onSignalStrengthChanged;
        UpdateIntervalMs = updateIntervalMs;
        SignalTimeoutMs = signalTimeoutMs;

        _updateTimer = new Timer(UpdateSignalStrengths, null,
            TimeSpan.FromMilliseconds(updateIntervalMs),
            TimeSpan.FromMilliseconds(updateIntervalMs));

        _noiseTimer = new Timer(UpdateNoiseFloor, null,
            TimeSpan.FromMilliseconds(noiseUpdateIntervalMs),
            TimeSpan.FromMilliseconds(noiseUpdateIntervalMs));
    }

    /// <summary>
    /// Update the signal strength for a given frequency.
    /// Call this from your audio processing pipeline.
    /// Only updates for signals above the noise floor (SNR > 0 dB).
    /// </summary>
    public void UpdateSignalStrength(int frequencyKhz, AudioParams audioParams)
    {
        // Ignore pure noise floor or below - these are not actual signals
        if (audioParams.ReceivedSnrDb <= 0)
            return;

        var strength = GetSignalStrength(audioParams);
        _signalStrengths.AddOrUpdate(
            frequencyKhz,
            new SignalStrengthData
            {
                SnrDb = audioParams.ReceivedSnrDb,
                Strength = strength,
                LastUpdate = DateTime.UtcNow
            },
            (_, existing) =>
            {
                existing.SnrDb = audioParams.ReceivedSnrDb;
                existing.Strength = strength;
                existing.LastUpdate = DateTime.UtcNow;
                return existing;
            });
    }

    /// <summary>
    /// Set the squelch state for a frequency.
    /// When squelch is open, noise floor updates will be generated.
    /// </summary>
    public void SetSquelchState(int frequencyKhz, bool isSquelchOpen)
    {
        if (isSquelchOpen)
        {
            _squelchStates[frequencyKhz] = true;
        }
        else
        {
            _squelchStates.TryRemove(frequencyKhz, out _);
        }
    }

    /// <summary>
    /// Remove tracking for a frequency (when untuning a radio).
    /// </summary>
    public void RemoveFrequency(int frequencyKhz)
    {
        _signalStrengths.TryRemove(frequencyKhz, out _);
        _squelchStates.TryRemove(frequencyKhz, out _);
    }

    /// <summary>
    /// Periodically update noise floor for frequencies with open squelch.
    /// Adds subtle random variation (±0.5 dB) to simulate atmospheric noise.
    /// </summary>
    private void UpdateNoiseFloor(object? state)
    {
        var now = DateTime.UtcNow;
        var timeout = TimeSpan.FromMilliseconds(SignalTimeoutMs);

        foreach (var frequencyKhz in _squelchStates.Keys)
        {
            if (_squelchStates[frequencyKhz])
            {
                // Check if there's a recent transmission - don't overwrite it with noise
                _signalStrengths.TryGetValue(frequencyKhz, out var existingData);
                bool hasRecentTransmission = existingData != null &&
                                             (now - existingData.LastUpdate) <= timeout;

                if (hasRecentTransmission)
                {
                    // Don't overwrite active/recent transmission with noise floor
                    continue;
                }

                // Add subtle random variation (±0.5 dB) around 0 dB to simulate atmospheric noise
                float variation = (float)Math.Sin(_noisePhase) * 1.5f;
                _noisePhase += 0.9f;
                float strength = ((float)_random.NextDouble() * 3f) + 0f; // 0–3%

                strength = Math.Clamp(strength, 0f, 100f);

                _signalStrengths.AddOrUpdate(
                    frequencyKhz,
                    new SignalStrengthData
                    {
                        SnrDb = variation, // ~0 dB ±1.5
                        Strength = strength, // 0-3% strength at noise floor
                        LastUpdate = DateTime.UtcNow
                    },
                    (_, existing) =>
                    {
                        existing.SnrDb = variation;
                        existing.Strength = strength;
                        existing.LastUpdate = DateTime.UtcNow;
                        return existing;
                    });
            }
        }
    }

    private void UpdateSignalStrengths(object? state)
    {
        var now = DateTime.UtcNow;
        var timeout = TimeSpan.FromMilliseconds(SignalTimeoutMs);

        var allFrequencies = _signalStrengths.Keys
            .Union(_squelchStates.Keys)
            .Distinct();

        foreach (var frequencyKhz in allFrequencies)
        {
            _signalStrengths.TryGetValue(frequencyKhz, out var data);
            _squelchStates.TryGetValue(frequencyKhz, out var squelchOpen);

            bool hasRecentTransmission = data != null && (now - data.LastUpdate) <= timeout;

            float strength, snrDb;

            if (hasRecentTransmission)
            {
                // Active transmission or recent noise update
                strength = data!.Strength;
                snrDb = data.SnrDb;
            }
            else if (squelchOpen)
            {
                // Squelch open, no transmission - show noise floor
                strength = 0f;
                snrDb = 0f; // At noise floor
            }
            else
            {
                // Squelch closed - no display
                strength = 0f;
                snrDb = 0f;
            }

            _onSignalStrengthChanged(frequencyKhz, (strength, snrDb));
        }
    }

    public void Dispose()
    {
        _updateTimer?.Dispose();
        _noiseTimer?.Dispose();
    }

    private static float GetSignalStrength(AudioParams audioParams)
    {
        // Map SNR to 0-100 range showing RF signal quality
        // -10 dB -> 0%   (below noise, unusable)
        // 0 dB   -> 20%  (at noise floor)
        // 6 dB   -> 32%  (squelch threshold - barely usable)
        // 20 dB  -> 60%  (good, reliable signal)
        // 40 dB  -> 100% (excellent, bulletproof signal)

        float snr = audioParams.ReceivedSnrDb;

        const float minSnr = -10f;
        const float maxSnr = 40f;
        float strength = ((snr - minSnr) / (maxSnr - minSnr)) * 100f;

        // Apply quality degradation based on dropout/fade rates
        float qualityFactor = 1.0f;

        if (audioParams.DropoutRate > 0.1f)
        {
            qualityFactor *= Math.Max(0.5f, 1.0f - (audioParams.DropoutRate * 0.3f));
        }

        if (audioParams.DeepFadeRate > 0.05f)
        {
            qualityFactor *= Math.Max(0.6f, 1.0f - (audioParams.DeepFadeRate * 0.5f));
        }

        strength *= qualityFactor;

        return Math.Clamp(strength, 0f, 100f);
    }

    public class SignalStrengthUpdateMessage
    {
        public int FrequencyKhz { get; }
        public float StrengthPercent { get; } // 0-100
        public float SnrDb { get; }

        public SignalStrengthUpdateMessage(int frequencyKhz, float strengthPercent, float snrDb)
        {
            FrequencyKhz = frequencyKhz;
            StrengthPercent = strengthPercent;
            SnrDb = snrDb;
        }
    }
}
