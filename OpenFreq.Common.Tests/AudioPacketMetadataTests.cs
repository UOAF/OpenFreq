using System.Text.Json;
using OpenFreq.Common;

namespace OpenFreq.Common.Tests;

public class AudioPacketMetadataTests
{
    [Fact]
    public void Defaults_FrequenciesEmpty_TimestampsZero()
    {
        var md = new AudioPacketMetadata { ClientId = "c1" };
        Assert.Empty(md.Frequencies);
        Assert.Equal(0, md.CaptureTimestamp);
        Assert.Equal(0, md.SendTimestamp);
        Assert.Equal(0, md.ServerSendTimestamp);
    }

    [Fact]
    public void JsonRoundTrip_PreservesScalarFields()
    {
        var md = new AudioPacketMetadata
        {
            ClientId = "viper-1",
            CaptureTimestamp = 111,
            SendTimestamp = 222,
            ServerSendTimestamp = 333
        };

        var json = JsonSerializer.Serialize(md, OpenFreqJsonContext.Default.AudioPacketMetadata);
        var back = JsonSerializer.Deserialize(json, OpenFreqJsonContext.Default.AudioPacketMetadata);

        Assert.NotNull(back);
        Assert.Equal("viper-1", back.ClientId);
        Assert.Equal(111, back.CaptureTimestamp);
        Assert.Equal(222, back.SendTimestamp);
        Assert.Equal(333, back.ServerSendTimestamp);
    }

    [Fact]
    public void JsonRoundTrip_PreservesFrequenciesAndPosition()
    {
        var md = new AudioPacketMetadata
        {
            ClientId = "c1",
            Frequencies =
            [
                new FrequencyTransmission(251000, 25.0, 1.0, new Vector3(1, 2, 3), null, true)
            ]
        };

        var json = JsonSerializer.Serialize(md, OpenFreqJsonContext.Default.AudioPacketMetadata);
        var back = JsonSerializer.Deserialize(json, OpenFreqJsonContext.Default.AudioPacketMetadata);

        Assert.NotNull(back);
        var freq = Assert.Single(back.Frequencies);
        Assert.Equal(251000, freq.Khz);
        Assert.Equal(25.0, freq.TxPowerWatts);
        Assert.True(freq.In3d);
        Assert.NotNull(freq.Position);
        Assert.Equal(1, freq.Position.X);
        Assert.Equal(2, freq.Position.Y);
        Assert.Equal(3, freq.Position.Z);
    }
}
