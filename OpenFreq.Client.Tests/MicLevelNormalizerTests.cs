using OpenFreqAudio;
using OpenFreqClient.Services;

namespace OpenFreq.Client.Tests;

public class MicLevelNormalizerTests
{
    private const int Fs = AudioFormat.SampleRate;
    private const int Block = 96; // the 2 ms BASS record period
    private const double TargetDb = -18;
    // The limiter's -1 dBFS ceiling.
    private const int MaxOutput = (int)(short.MaxValue * 0.891) + 1;

    /// <summary>White Gaussian noise at an RMS of <paramref name="dbfs"/>, clipped to 16 bits like a sound card.</summary>
    private static short[] Noise(double seconds, double dbfs, int seed = 1)
    {
        var random = new Random(seed);
        double rms = Math.Pow(10, dbfs / 20) * short.MaxValue;
        var samples = new short[(int)(seconds * Fs)];
        for (int i = 0; i < samples.Length; i++)
        {
            double gaussian = Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
            samples[i] = (short)Math.Clamp(gaussian * rms, -short.MaxValue, short.MaxValue);
        }

        return samples;
    }

    private static short[] Zeros(double seconds) => new short[(int)(seconds * Fs)];

    private static void Idle(MicLevelNormalizer normalizer, short[] samples)
    {
        for (int o = 0; o < samples.Length; o += Block)
            normalizer.UpdateNoiseFloor(samples.AsSpan(o, Math.Min(Block, samples.Length - o)));
    }

    /// <summary>Runs the samples through <see cref="MicLevelNormalizer.Process"/> in record-sized blocks.</summary>
    private static short[] Talk(MicLevelNormalizer normalizer, short[] samples)
    {
        var output = new short[samples.Length];
        var block = new short[Block];
        for (int o = 0; o < samples.Length; o += Block)
        {
            int n = Math.Min(Block, samples.Length - o);
            Array.Copy(samples, o, block, 0, n);
            normalizer.Process(block, n);
            Array.Copy(block, 0, output, o, n);
        }

        return output;
    }

    private static double RmsDb(ReadOnlySpan<short> samples)
    {
        double sum = 0;
        foreach (var s in samples) sum += (s / (double)short.MaxValue) * (s / (double)short.MaxValue);
        return 10 * Math.Log10(sum / samples.Length);
    }

    private static int MaxMagnitude(short[] samples) => samples.Max(s => Math.Abs((int)s));

    [Theory]
    [InlineData(-35)] // a quiet mic: +17 dB, under the cap
    [InlineData(-10)] // a hot mic: -8 dB
    public void SteadyInput_ReachesTarget(double inputDb)
    {
        var normalizer = new MicLevelNormalizer(Fs);
        Idle(normalizer, Noise(3, -70));
        normalizer.BeginTalkspurt();

        var output = Talk(normalizer, Noise(5, inputDb, seed: 2));

        Assert.InRange(RmsDb(output.AsSpan(^Fs..)), TargetDb - 1, TargetDb + 1);
    }

    [Fact]
    public void DigitalSilence_GateStopsAtMinimum()
    {
        var normalizer = new MicLevelNormalizer(Fs);
        Idle(normalizer, Zeros(5));

        Assert.Equal(0.001f, normalizer.NoiseGateRms); // -60 dBFS

        // The RMS detector starts at the target and moves the gain while it decays to the
        // input level. After that, the input is below the gate, so the gain must not move.
        normalizer.BeginTalkspurt();
        Talk(normalizer, Noise(1, -70));
        float settledGain = normalizer.CurrentGain;
        Talk(normalizer, Noise(2, -70, seed: 2));
        Assert.Equal(settledGain, normalizer.CurrentGain);
    }

    [Fact]
    public void QuietInput_GainStopsAtCap()
    {
        var normalizer = new MicLevelNormalizer(Fs);
        Idle(normalizer, Zeros(5));
        normalizer.BeginTalkspurt();

        // Just above the gate, asking for +40 dB.
        Talk(normalizer, Noise(5, -58));

        Assert.InRange(normalizer.CurrentGain, 9f, 10.001f);
    }

    [Fact]
    public void GainRises_AtTheSameRateInDb()
    {
        var normalizer = new MicLevelNormalizer(Fs);
        Idle(normalizer, Zeros(5));
        normalizer.BeginTalkspurt();

        // Asks for more than the +20 dB cap. With a 1 s time constant in dB, the gain is about
        // 63% of the way (+12.6 dB) after 1 s. A ramp on linear gain would be at +16.5 dB.
        Talk(normalizer, Noise(1, -45));

        double gainDb = 20 * Math.Log10(normalizer.CurrentGain);
        Assert.InRange(gainDb, 11, 14);
    }

    [Fact]
    public void LoudOnsetAfterSilence_DoesNotClip()
    {
        // The old failure: a quiet room collapses the gate, the lead-in after PTT drives the gain
        // up, and the first word goes out at full scale.
        var normalizer = new MicLevelNormalizer(Fs);
        Idle(normalizer, Zeros(5));
        normalizer.BeginTalkspurt();

        Talk(normalizer, Noise(3, -58));
        var output = Talk(normalizer, Noise(0.5, -10, seed: 3));

        Assert.InRange(MaxMagnitude(output), short.MaxValue / 2, MaxOutput);
    }

    [Fact]
    public void LoudAfterSoftSpeech_DoesNotClip()
    {
        var normalizer = new MicLevelNormalizer(Fs);
        Idle(normalizer, Noise(3, -60));
        normalizer.BeginTalkspurt();

        var soft = Talk(normalizer, Noise(1, -45));
        var loud = Talk(normalizer, Noise(0.5, -10, seed: 3));

        Assert.True(MaxMagnitude(soft) <= MaxOutput);
        Assert.True(MaxMagnitude(loud) <= MaxOutput);
    }

    [Fact]
    public void BeginTalkspurt_DropsTheLastTalkspurtsAudio()
    {
        var normalizer = new MicLevelNormalizer(Fs);
        Idle(normalizer, Noise(3, -60));
        normalizer.BeginTalkspurt();
        Talk(normalizer, Noise(0.2, -20));

        normalizer.BeginTalkspurt();
        var output = Talk(normalizer, Zeros(0.02));

        Assert.All(output, s => Assert.Equal(0, s));
    }

    [Fact]
    public void LimiterStatistics_ResetPerTalkspurt()
    {
        var normalizer = new MicLevelNormalizer(Fs);
        Idle(normalizer, Zeros(5));
        normalizer.BeginTalkspurt();
        Talk(normalizer, Noise(3, -58));
        Talk(normalizer, Noise(0.5, -10, seed: 3));

        Assert.True(normalizer.LimitedSamples > 0);
        Assert.True(normalizer.MaxLimiterReductionDb > 0);

        normalizer.BeginTalkspurt();
        Assert.Equal(0, normalizer.LimitedSamples);
        Assert.Equal(0f, normalizer.MaxLimiterReductionDb);
    }
}
