using System;

namespace OpenFreqClient.Services;

/// <summary>
/// Transmit-side loudness normalizer for the microphone path. Combats the wide
/// per-user volume spread (loud mics vs. quiet mics) by slowly steering
/// each users average level toward a common reference, then brick-wall.
///
/// This is not to be confused by the AGC in the playback loop, it is only used on the TX side.
/// Survives multiple talk-spurts, initial levelling needs about 1sec.
/// </summary>
public sealed class MicLevelNormalizer(int sampleRate)
{
    // Target average level
    private const float TargetRms = 0.1f; // ~-20 dBFS

    // Gain range
    private const float MinGain = 0.25f; // -12 dB
    private const float MaxGain = 10.0f; // +20 dB

    // Noise floor
    private const float NoiseGateRms = 0.005f; // ~-46 dBFS

    // Brick-wall ceiling just below full scale
    private const float LimitCeiling = 0.97f; // ~-0.26 dBFS

    // One-pole smoothing coefficients
    private readonly float _rmsAlpha = 1.0f - MathF.Exp(-1.0f / (0.3f * sampleRate));  // RMS envelope, ~300 ms
    private readonly float _gainAlpha = 1.0f - MathF.Exp(-1.0f / (1.0f * sampleRate)); // gain ramp, ~1 s

    // Seed the envelope at the target so startup doesn't lurch
    private float _envSq = TargetRms * TargetRms;        // smoothed mean-square of the signal
    private float _gain = 1.0f;  // current applied gain

    /// <summary>
    /// Normalizes <paramref name="count"/> mono 16-bit PCM samples in place.
    /// </summary>
    public void Process(short[] samples, int count)
    {
        for (int i = 0; i < count; i++)
        {
            float x = samples[i] / 32768f;

            // Track the smoothed RMS of the input.
            _envSq += _rmsAlpha * (x * x - _envSq);
            float rms = MathF.Sqrt(_envSq);

            // Only chase a new gain target when there's actual speech present;
            // otherwise hold the last gain so silence isn't pumped up.
            float desiredGain = rms > NoiseGateRms
                ? Math.Clamp(TargetRms / rms, MinGain, MaxGain)
                : _gain;

            // Slowly ramp toward the target
            _gain += _gainAlpha * (desiredGain - _gain);

            float y = x * _gain;

            // Brick-wall limiter, we don't want any overshoots
            if (y > LimitCeiling) y = LimitCeiling;
            else if (y < -LimitCeiling) y = -LimitCeiling;

            samples[i] = (short)(y * 32767f);
        }
    }
}
