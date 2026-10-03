using System;
using OpenFreqAudio;

namespace OpenFreqClient.Services;

/// <summary>
/// Transmit-side normalizer for player mics, which vary greatly in volume and noise floor.
/// Steers average volume towards a target RMS and limits peaks to keep us from clipping.
///
/// Not to be confused with automatic gain control in the RF simulation,
/// which models how a radio handles a weak signal.
///
/// The noise gate is dynamic - whenever the player isn't transmitting,
/// <see cref="UpdateNoiseFloor"/> tracks the room's noise floor.
/// That floor is frozen whenever the user starts talking to prevent wild volume fluctuations
/// in between words.
/// </summary>
public sealed class MicLevelNormalizer
{
    // Target average volume level (root mean squared).
    // Human speech has a peak-to-average power level of about 12-18 dB, so shoot for -18 dBFS.
    private const float TargetRms = 0.126f;

    // Avoid boosting more than this to prevent clipping if an excitable gamer suddenly
    // starts talking loudly.
    private const float MaxGain = 10f; // +20 dB

    // 16-bit PCM full-scale magnitude; maps samples to/from float in ~[-1, 1].
    private const float SampleScale = short.MaxValue;

    // Prevent the noise gate from dropping too low in quiet rooms,
    // or with mics with built-in noise suppression.
    private const float MinGateRms = 0.001f; // -60 dBFS

    // RMS low-pass time constant
    private const float LevelTau = 0.1f / 3f; // ~33ms, 95% in 100ms

    // We want our gain to duck loud inputs quickly,
    // but rise again slowly so that when people push-to-think (boo!)
    // it doesn't crank their volume up and clip once they actually speak.
    // The ramp runs on ln(gain), so it rises equally slowly in dB at any gain.
    // (On a linear gain, a large target made the "slow" rise many dB per second.)
    private const float GainRiseTau = 1.0f;
    private const float GainFallTau = 0.05f;

    // Noise floor: slow to rise, quick to fall, so brief non-PTT transients (a cough, a door)
    // don't crank the floor up while it still settles back down to true quiet.
    private const float FloorAttackTau = 1.0f; // rising
    private const float FloorDecayTau = 0.3f;  // falling

    // TX-path smoothers
    private readonly FirstOrderFilter _level; // Input RMS
    private readonly AttackDecayFilter _gainRamp; // smoothed ln(applied gain)

    private readonly PeakLimiter _limiter;

    // Idle-mic noise-floor follower, in RMS.
    private readonly AttackDecayFilter _noiseFloor;

    /// <summary>
    /// Current noise-gate threshold (linear RMS). Recomputed from the live mic by
    /// <see cref="UpdateNoiseFloor"/> while idle, then held constant during transmission.
    /// </summary>
    public float NoiseGateRms { get; private set; } = MinGateRms;

    /// <summary>
    /// Gain <see cref="Process"/> is currently applying (linear, 1.0 = unity), before the limiter.
    /// Held constant while the input sits below <see cref="NoiseGateRms"/>, so a gate that stops
    /// opening leaves this frozen at whatever the last talk-spurt ducked it to — which is worth
    /// being able to see.
    /// </summary>
    public float CurrentGain => MathF.Exp(_gainRamp.D1);

    /// <summary>Samples the limiter turned down since <see cref="BeginTalkspurt"/>.</summary>
    public int LimitedSamples => _limiter.LimitedSamples;

    /// <summary>Largest limiter gain reduction since <see cref="BeginTalkspurt"/>, in dB (positive).</summary>
    public float MaxLimiterReductionDb => _limiter.MaxReductionDb;

    public MicLevelNormalizer(int sampleRate)
    {
        _level = FirstOrderFilter.MakeFirstOrderFilter(LevelTau, sampleRate, TargetRms * TargetRms);
        _gainRamp = AttackDecayFilter.MakeAttackDecayFilter(GainRiseTau, GainFallTau, sampleRate);
        _gainRamp.D1 = 0; // ln(1): start at unity gain
        _limiter = new PeakLimiter(sampleRate);
        _noiseFloor = AttackDecayFilter.MakeAttackDecayFilter(FloorAttackTau, FloorDecayTau, sampleRate);
        // Seed in the mean-square domain so early PTT matches the old fixed gate.
        _noiseFloor.D1 = MinGateRms * MinGateRms;
    }

    /// <summary>
    /// Call before the first <see cref="Process"/> of each talk-spurt to avoid reusing
    /// the previous transmission's lookahead buffer.
    /// </summary>
    public void BeginTalkspurt() => _limiter.Reset();

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
        // Add some margin to gate 6 dB above the measured noise floor.
        NoiseGateRms = MathF.Max(floorRms * 2.0f, MinGateRms);
    }

    /// <summary>
    /// Normalizes <paramref name="count"/> mono 16-bit PCM samples in place.
    /// Output lags the input by a 2ms for the limiter's lookahead.
    /// </summary>
    public void Process(short[] samples, int count)
    {
        for (int i = 0; i < count; i++)
        {
            float x = samples[i] / SampleScale;

            // Track the smoothed RMS of the input.
            float rms = MathF.Sqrt(_level.Apply(x * x));

            // Only adjust the gain when there's actual speech (above the frozen noise floor).
            float desiredLogGain = rms > NoiseGateRms
                ? MathF.Log(MathF.Min(TargetRms / rms, MaxGain))
                : _gainRamp.D1;

            // Slowly ramp toward the target
            float gain = MathF.Exp(_gainRamp.Apply(desiredLogGain));

            // The limiter should keep us below full scale; the clamp is a last guard.
            float y = Math.Clamp(_limiter.Process(x * gain), -1, 1);
            samples[i] = (short)(y * SampleScale);
        }
    }
}
