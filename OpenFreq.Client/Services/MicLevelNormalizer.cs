using System;
using OpenFreqAudio;

namespace OpenFreqClient.Services;

/// <summary>
/// Transmit-side loudness normalizer for the microphone path. Combats the wide
/// per-user volume spread (loud mics vs. quiet mics) by slowly steering
/// each users average level toward a common reference, then brick-wall.
///
/// This is not to be confused by the AGC in the playback loop, it is only used on the TX side.
/// Survives multiple talk-spurts, initial levelling needs about 1sec.
///
/// The noise gate is dynamic: while the operator is NOT transmitting the raw mic is fed to
/// <see cref="UpdateNoiseFloor"/>, which tracks the room's noise floor. That value is then
/// frozen and used as the gate threshold for the duration of the next talk-spurt, so we don't
/// chase gain on background hiss.
/// </summary>
public sealed class MicLevelNormalizer
{
    // Target average level (root mean squared).
    // Human speach has a peak-to-average power level
    // of about 12-18 dB, so we want to be about that many
    // below full strength.
    private const float TargetRms = 0.1f; // ~20 dBFS

    // 16-bit PCM full-scale magnitude; maps samples to/from float in ~[-1, 1].
    private const float SampleScale = short.MaxValue;

    // Noise gate. The threshold is tracked from the live mic while idle (UpdateNoiseFloor)
    // then frozen during transmission. NoiseFloorSeedRms doubles as the startup seed so the
    // first talk-spurt behaves like the old fixed gate before the estimate has warmed up.
    private const float NoiseFloorSeedRms = 0.005f; // ~-46 dBFS

    // RMS low-pass (it's a mean, after all)
    private const float LevelTau = 0.1f / 3f; // ~33ms, 95% in 100ms

    // We want our gain to duck loud inputs quickly,
    // but rise again slowly so that when people push-to-think (boo!)
    // it doesn't crank their volume up and clip once they actually speak.
    private const float GainRiseTau = 1.0f;
    private const float GainFallTau = 0.05f;

    // Noise floor: slow to rise, quick to fall, so brief non-PTT transients (a cough, a
    // door) don't ratchet the floor up while it still settles back down to true quiet.
    private const float FloorAttackTau = 1.0f; // rising
    private const float FloorDecayTau = 0.3f;  // falling

    // TX-path smoothers
    private readonly FirstOrderFilter _level; // Input RMS
    private readonly AttackDecayFilter _gainRamp; // smoothed applied gain

    // Idle-mic noise-floor follower. Holds the smoothed mean-square (so the asymmetric
    // attack/decay compares like-for-like); sqrt of its tap is the floor RMS.
    private readonly AttackDecayFilter _noiseFloor;

    /// <summary>
    /// Current noise-gate threshold (linear RMS). Recomputed from the live mic by
    /// <see cref="UpdateNoiseFloor"/> while idle, then held constant during transmission.
    /// </summary>
    public float NoiseGateRms { get; private set; } = NoiseFloorSeedRms;

    public MicLevelNormalizer(int sampleRate)
    {
        _level = FirstOrderFilter.MakeFirstOrderFilter(LevelTau, sampleRate, TargetRms * TargetRms);
        _gainRamp = AttackDecayFilter.MakeAttackDecayFilter(GainRiseTau, GainFallTau, sampleRate);
        _noiseFloor = AttackDecayFilter.MakeAttackDecayFilter(FloorAttackTau, FloorDecayTau, sampleRate);
        // Seed in the mean-square domain so early PTT matches the old fixed gate.
        _noiseFloor.D1 = NoiseFloorSeedRms * NoiseFloorSeedRms;
    }

    /// <summary>
    /// Advances the noise-floor estimate from idle-mic samples. Call this with raw mic audio
    /// whenever the operator is NOT transmitting; the resulting <see cref="NoiseGateRms"/> is
    /// then frozen and used by <see cref="Process"/> during the next talk-spurt.
    /// </summary>
    public void UpdateNoiseFloor(ReadOnlySpan<short> samples)
    {
        foreach (short sample in samples)
        {
            float x = sample / SampleScale;
            _noiseFloor.Apply(x * x);
        }

        float floorRms = MathF.Sqrt(_noiseFloor.D1);
        // Add some margin to gate ~3dB above the measured noise floor.
        NoiseGateRms = floorRms * 2.0f;
    }

    /// <summary>
    /// Normalizes <paramref name="count"/> mono 16-bit PCM samples in place.
    /// </summary>
    public void Process(short[] samples, int count)
    {
        for (int i = 0; i < count; i++)
        {
            float x = samples[i] / SampleScale;

            // Track the smoothed RMS of the input.
            float rms = MathF.Sqrt(_level.Apply(x * x));

            // Only chase a new gain target when there's actual speech present (above the
            // frozen noise gate); otherwise hold the last gain so silence isn't pumped up.
            float desiredGain = rms > NoiseGateRms
                ? TargetRms / rms
                : _gainRamp.D1;

            // Slowly ramp toward the target
            float gain = _gainRamp.Apply(desiredGain);

            float y = Math.Clamp(x * gain, -1, 1);
            samples[i] = (short)(y * SampleScale);
        }
    }
}
