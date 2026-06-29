using System;
using Microsoft.Extensions.Logging;
using OpenFreqAudio;
using OpenFreqAudio.TerrainSampling;

namespace OpenFreqClient.Services.Audio;

/// <summary>
/// Abstraction over the terrain-aware RF physics (<see cref="HeightPyramid"/> + <see cref="FastPathAudioSim"/>).
/// Exists so signal calculation can be faked in tests.
/// </summary>
public interface ISignalCalculator : IDisposable
{
    AudioParams CalculateAudioParams(
        double? txX, double? txY, double? txAlt,
        double? rxX, double? rxY, double? rxAlt,
        int frequencyKhz,
        float ppm = 0.0f,
        double txPowerWatts = 10.0,
        double? receiverSensitivityDbm = null,
        bool includeTerrainProfile = false,
        bool txAltitudeIsMSL = false,
        bool rxAltitudeIsMSL = false,
        (double x, double y, double z)? txVelocity = null,
        (double x, double y, double z)? rxVelocity = null);
    public double SampleElevation(double xMeters, double yMeters);

}

/// <summary>Adapter owning a <see cref="DEMReader"/> + <see cref="FastPathAudioSim"/>.</summary>
public sealed class TerrainSignalCalculator : ISignalCalculator
{
    private readonly HeightPyramid _pyramid;
    private readonly FastPathAudioSim _sim;

    public TerrainSignalCalculator(string path, int width, int height, int bytesPerSample, double cellSizeMeters,
        ILoggerFactory loggerFactory)
    {
        if (bytesPerSample != 2)
            throw new ArgumentException("HeightPyramid only supports 2-byte (int16 feet) samples", nameof(bytesPerSample));
        _pyramid = HeightPyramid.FromFile(path, width, height);
        _sim = new FastPathAudioSim(_pyramid, 0, 0, cellSizeMeters, loggerFactory.CreateLogger<FastPathAudioSim>());
    }

    public AudioParams CalculateAudioParams(
        double? txX, double? txY, double? txAlt,
        double? rxX, double? rxY, double? rxAlt,
        int frequencyKhz,
        float ppm = 0.0f,
        double txPowerWatts = 10.0,
        double? receiverSensitivityDbm = null,
        bool includeTerrainProfile = false,
        bool txAltitudeIsMSL = false,
        bool rxAltitudeIsMSL = false,
        (double x, double y, double z)? txVelocity = null,
        (double x, double y, double z)? rxVelocity = null)
        => _sim.CalculateAudioParams(txX, txY, txAlt, rxX, rxY, rxAlt, frequencyKhz, ppm, txPowerWatts,
            receiverSensitivityDbm, includeTerrainProfile, txAltitudeIsMSL, rxAltitudeIsMSL, txVelocity, rxVelocity);
    public double SampleElevation(double xMeters, double yMeters) => _sim.SampleElevation(xMeters, yMeters);

    public void Dispose() => _pyramid.Dispose();
}

/// <summary>
/// Creates <see cref="ISignalCalculator"/> instances. Default implementation loads a real heightmap;
/// tests substitute a fake factory.
/// </summary>
public interface ISignalCalculatorFactory
{
    ISignalCalculator Create(string path, int width, int height, int bytesPerSample, double cellSizeMeters,
        ILoggerFactory loggerFactory);
}

/// <summary>Default factory producing the terrain-backed calculator.</summary>
public sealed class TerrainSignalCalculatorFactory : ISignalCalculatorFactory
{
    public ISignalCalculator Create(string path, int width, int height, int bytesPerSample, double cellSizeMeters,
        ILoggerFactory loggerFactory)
        => new TerrainSignalCalculator(path, width, height, bytesPerSample, cellSizeMeters, loggerFactory);
}
