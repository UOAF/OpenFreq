using OpenFreq.Common.Rtp;

namespace OpenFreq.Common.Tests;

public class RtpPacketTests
{
    private static byte[] BuildRtpBytes(
        byte version = 2,
        bool padding = false,
        bool extension = false,
        byte csrcCount = 0,
        bool marker = false,
        byte payloadType = 111,
        ushort seq = 1,
        uint timestamp = 1000,
        uint ssrc = 0xDEADBEEF,
        ushort extensionProfile = RtpPacket.OpenFreqProfile,
        byte[]? extensionData = null,
        byte[]? payload = null)
    {
        bool hasExt = extension && extensionData is { Length: > 0 };
        int paddedExtLen = hasExt ? (extensionData!.Length + 3) & ~3 : 0;
        int totalLen = 12 + (hasExt ? 4 + paddedExtLen : 0) + (payload?.Length ?? 0);
        byte[] data = new byte[totalLen];

        data[0] = (byte)((version << 6) | (padding ? 0x20 : 0) | (hasExt ? 0x10 : 0) | csrcCount);
        data[1] = (byte)((marker ? 0x80 : 0) | (payloadType & 0x7F));
        data[2] = (byte)(seq >> 8);
        data[3] = (byte)(seq & 0xFF);
        data[4] = (byte)(timestamp >> 24);
        data[5] = (byte)((timestamp >> 16) & 0xFF);
        data[6] = (byte)((timestamp >> 8) & 0xFF);
        data[7] = (byte)(timestamp & 0xFF);
        data[8] = (byte)(ssrc >> 24);
        data[9] = (byte)((ssrc >> 16) & 0xFF);
        data[10] = (byte)((ssrc >> 8) & 0xFF);
        data[11] = (byte)(ssrc & 0xFF);

        int offset = 12;
        if (hasExt)
        {
            int wordLen = paddedExtLen / 4;
            data[offset] = (byte)(extensionProfile >> 8);
            data[offset + 1] = (byte)(extensionProfile & 0xFF);
            data[offset + 2] = (byte)(wordLen >> 8);
            data[offset + 3] = (byte)(wordLen & 0xFF);
            offset += 4;
            Array.Copy(extensionData!, 0, data, offset, extensionData!.Length);
            offset += paddedExtLen;
        }

        if (payload != null)
            Array.Copy(payload, 0, data, offset, payload.Length);

        return data;
    }

    [Fact]
    public void Parse_ValidMinimalPacket_ReturnsCorrectFields()
    {
        byte[] data = BuildRtpBytes(seq: 42, timestamp: 96000, ssrc: 0x12345678, payloadType: 111);

        var packet = RtpPacket.Parse(data);

        Assert.NotNull(packet);
        Assert.Equal(2, packet.Version);
        Assert.Equal(42, packet.SequenceNumber);
        Assert.Equal(96000u, packet.Timestamp);
        Assert.Equal(0x12345678u, packet.Ssrc);
        Assert.Equal(111, packet.PayloadType);
    }

    [Fact]
    public void Parse_PacketTooShort_ReturnsNull()
    {
        byte[] data = new byte[11];
        Assert.Null(RtpPacket.Parse(data));
    }

    [Fact]
    public void Parse_EmptyArray_ReturnsNull()
    {
        Assert.Null(RtpPacket.Parse([]));
    }

    [Fact]
    public void Parse_VersionNotTwo_ReturnsNull()
    {
        byte[] data = BuildRtpBytes(version: 1);
        Assert.Null(RtpPacket.Parse(data));
    }

    [Fact]
    public void Parse_VersionZero_ReturnsNull()
    {
        byte[] data = BuildRtpBytes(version: 0);
        Assert.Null(RtpPacket.Parse(data));
    }

    [Fact]
    public void Parse_MarkerBitTrue_ParsedCorrectly()
    {
        byte[] data = BuildRtpBytes(marker: true);
        Assert.True(RtpPacket.Parse(data)!.Marker);
    }

    [Fact]
    public void Parse_MarkerBitFalse_ParsedCorrectly()
    {
        byte[] data = BuildRtpBytes(marker: false);
        Assert.False(RtpPacket.Parse(data)!.Marker);
    }

    [Fact]
    public void Parse_WithPayload_ExtractsPayload()
    {
        byte[] payload = [0xAA, 0xBB, 0xCC];
        byte[] data = BuildRtpBytes(payload: payload);

        var packet = RtpPacket.Parse(data);

        Assert.NotNull(packet);
        Assert.Equal(payload, packet.Payload);
    }

    [Fact]
    public void Parse_NoPayload_EmptyPayloadArray()
    {
        byte[] data = BuildRtpBytes();

        var packet = RtpPacket.Parse(data);

        Assert.NotNull(packet);
        Assert.Empty(packet.Payload);
    }

    [Fact]
    public void Parse_WithExtension_ParsesProfileAndData()
    {
        byte[] extData = [0x01, 0x02, 0x03, 0x04];
        byte[] data = BuildRtpBytes(
            extension: true,
            extensionProfile: RtpPacket.OpenFreqProfile,
            extensionData: extData);

        var packet = RtpPacket.Parse(data);

        Assert.NotNull(packet);
        Assert.Equal(RtpPacket.OpenFreqProfile, packet.ExtensionProfile);
        Assert.Equal(extData, packet.ExtensionData);
    }

    [Fact]
    public void Parse_WithExtension_PayloadAfterExtension()
    {
        byte[] extData = [0x10, 0x20, 0x30, 0x40];
        byte[] payload = [0xDE, 0xAD];
        byte[] data = BuildRtpBytes(
            extension: true,
            extensionData: extData,
            payload: payload);

        var packet = RtpPacket.Parse(data);

        Assert.NotNull(packet);
        Assert.Equal(extData, packet.ExtensionData);
        Assert.Equal(payload, packet.Payload);
    }

    [Fact]
    public void Parse_ExtensionTruncated_ReturnsNull()
    {
        // Build valid packet with extension then truncate
        byte[] full = BuildRtpBytes(extension: true, extensionData: [0x01, 0x02, 0x03, 0x04]);
        byte[] truncated = full[..13]; // cuts into extension header

        Assert.Null(RtpPacket.Parse(truncated));
    }

    [Fact]
    public void ToBytes_RoundTrip_PreservesAllFields()
    {
        var original = new RtpPacket
        {
            Version = 2,
            Marker = true,
            PayloadType = 111,
            SequenceNumber = 12345,
            Timestamp = 960000,
            Ssrc = 0xABCD1234,
            Payload = [0x01, 0x02, 0x03, 0x04, 0x05]
        };

        var parsed = RtpPacket.Parse(original.ToBytes());

        Assert.NotNull(parsed);
        Assert.Equal(original.Version, parsed.Version);
        Assert.Equal(original.Marker, parsed.Marker);
        Assert.Equal(original.PayloadType, parsed.PayloadType);
        Assert.Equal(original.SequenceNumber, parsed.SequenceNumber);
        Assert.Equal(original.Timestamp, parsed.Timestamp);
        Assert.Equal(original.Ssrc, parsed.Ssrc);
        Assert.Equal(original.Payload, parsed.Payload);
    }

    [Fact]
    public void ToBytes_WithExtension_RoundTrip()
    {
        var original = new RtpPacket
        {
            SequenceNumber = 1,
            Timestamp = 100,
            Ssrc = 0x1,
            ExtensionProfile = RtpPacket.OpenFreqProfile,
            ExtensionData = [0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80],
            Payload = [0xFF]
        };

        var parsed = RtpPacket.Parse(original.ToBytes());

        Assert.NotNull(parsed);
        Assert.Equal(RtpPacket.OpenFreqProfile, parsed.ExtensionProfile);
        Assert.Equal(original.ExtensionData, parsed.ExtensionData);
        Assert.Equal(original.Payload, parsed.Payload);
    }

    [Fact]
    public void ToBytes_Extension_PadsToFourByteWordBoundary()
    {
        // 3 bytes → padded to 4
        var original = new RtpPacket
        {
            ExtensionProfile = 0x1234,
            ExtensionData = [0xAA, 0xBB, 0xCC],
            Payload = []
        };

        var parsed = RtpPacket.Parse(original.ToBytes());

        Assert.NotNull(parsed);
        Assert.NotNull(parsed.ExtensionData);
        Assert.True(parsed.ExtensionData.Length >= 3);
        Assert.Equal(0xAA, parsed.ExtensionData[0]);
        Assert.Equal(0xBB, parsed.ExtensionData[1]);
        Assert.Equal(0xCC, parsed.ExtensionData[2]);
    }

    [Fact]
    public void ToBytes_SequenceNumberMaxValue_RoundTrips()
    {
        var original = new RtpPacket { SequenceNumber = ushort.MaxValue, Payload = [] };
        var parsed = RtpPacket.Parse(original.ToBytes());

        Assert.NotNull(parsed);
        Assert.Equal(ushort.MaxValue, parsed.SequenceNumber);
    }

    [Fact]
    public void ToBytes_TimestampMaxValue_RoundTrips()
    {
        var original = new RtpPacket { Timestamp = uint.MaxValue, Payload = [] };
        var parsed = RtpPacket.Parse(original.ToBytes());

        Assert.NotNull(parsed);
        Assert.Equal(uint.MaxValue, parsed.Timestamp);
    }

    [Fact]
    public void OpenFreqProfile_IsCorrectValue()
    {
        Assert.Equal(0x4F46, RtpPacket.OpenFreqProfile);
    }

    [Fact]
    public void HeaderSize_IsCorrectValue()
    {
        Assert.Equal(12, RtpPacket.HEADER_SIZE);
    }
}
