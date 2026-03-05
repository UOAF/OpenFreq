namespace OpenFreq.Common.Rtp;

public class RtpPacket
{
    // RTP Header Fields (12 bytes)
    public byte Version { get; set; } = 2;
    public bool Padding { get; set; }
    public byte CsrcCount { get; set; }
    public bool Marker { get; set; }  // Standard RTP marker bit (currently unused)
    public byte PayloadType { get; set; }
    public ushort SequenceNumber { get; set; }
    public uint Timestamp { get; set; }
    public uint Ssrc { get; set; }

    // RTP Header Extension (RFC 3550 §5.3.1) — present when ExtensionData is non-null/non-empty
    public ushort ExtensionProfile { get; set; }
    public byte[]? ExtensionData { get; set; }

    // Payload (pure audio data)
    public byte[] Payload { get; set; } = Array.Empty<byte>();

    public const int HEADER_SIZE = 12;
    public const ushort OpenFreqProfile = 0x4F46; // "OF" - OpenFreq
    
    /// <summary>
    /// Parse RTP packet from bytes
    /// </summary>
    public static RtpPacket? Parse(byte[] data)
    {
        if (data.Length < HEADER_SIZE)
            return null;
        
        var packet = new RtpPacket();
        
        // Byte 0: V(2), P(1), X(1), CC(4)
        packet.Version = (byte)((data[0] >> 6) & 0x03);
        packet.Padding = (data[0] & 0x20) != 0;
        bool hasExtension = (data[0] & 0x10) != 0;
        packet.CsrcCount = (byte)(data[0] & 0x0F);

        // Byte 1: M(1), PT(7) - Standard RTP format
        packet.Marker = (data[1] & 0x80) != 0;
        packet.PayloadType = (byte)(data[1] & 0x7F);

        // Bytes 2-3: Sequence number (big-endian)
        packet.SequenceNumber = (ushort)((data[2] << 8) | data[3]);

        // Bytes 4-7: Timestamp (big-endian)
        packet.Timestamp = (uint)((data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7]);

        // Bytes 8-11: SSRC (big-endian)
        packet.Ssrc = (uint)((data[8] << 24) | (data[9] << 16) | (data[10] << 8) | data[11]);

        // Validate version
        if (packet.Version != 2)
            return null;

        // Calculate data start (fixed header + optional CSRC list)
        int offset = HEADER_SIZE + (packet.CsrcCount * 4);

        // Parse header extension if present (RFC 3550 §5.3.1)
        if (hasExtension)
        {
            if (data.Length < offset + 4)
                return null;
            packet.ExtensionProfile = (ushort)((data[offset] << 8) | data[offset + 1]);
            int wordLength = (data[offset + 2] << 8) | data[offset + 3];
            int extByteLength = wordLength * 4;
            offset += 4;
            if (data.Length < offset + extByteLength)
                return null;
            packet.ExtensionData = new byte[extByteLength];
            Array.Copy(data, offset, packet.ExtensionData, 0, extByteLength);
            offset += extByteLength;
        }

        // Extract payload
        int payloadLength = data.Length - offset;
        packet.Payload = new byte[payloadLength];
        if (payloadLength > 0)
            Array.Copy(data, offset, packet.Payload, 0, payloadLength);

        return packet;
    }
    
    /// <summary>
    /// Serialize RTP packet to bytes
    /// </summary>
    public byte[] ToBytes()
    {
        bool hasExtension = ExtensionData is { Length: > 0 };
        int paddedExtLength = hasExtension ? (ExtensionData!.Length + 3) & ~3 : 0;
        int totalLength = HEADER_SIZE + (hasExtension ? 4 + paddedExtLength : 0) + Payload.Length;
        byte[] data = new byte[totalLength];

        // Byte 0: V(2), P(1), X(1), CC(4)
        data[0] = (byte)((Version << 6) | (Padding ? 0x20 : 0) | (hasExtension ? 0x10 : 0) | CsrcCount);

        // Byte 1: M(1), PT(7) - Standard RTP format
        data[1] = (byte)((Marker ? 0x80 : 0) | (PayloadType & 0x7F));

        // Bytes 2-3: Sequence number (big-endian)
        data[2] = (byte)(SequenceNumber >> 8);
        data[3] = (byte)(SequenceNumber & 0xFF);

        // Bytes 4-7: Timestamp (big-endian)
        data[4] = (byte)(Timestamp >> 24);
        data[5] = (byte)((Timestamp >> 16) & 0xFF);
        data[6] = (byte)((Timestamp >> 8) & 0xFF);
        data[7] = (byte)(Timestamp & 0xFF);

        // Bytes 8-11: SSRC (big-endian)
        data[8] = (byte)(Ssrc >> 24);
        data[9] = (byte)((Ssrc >> 16) & 0xFF);
        data[10] = (byte)((Ssrc >> 8) & 0xFF);
        data[11] = (byte)(Ssrc & 0xFF);

        int offset = HEADER_SIZE;

        // Header extension (RFC 3550 §5.3.1)
        if (hasExtension)
        {
            int wordLength = paddedExtLength / 4;
            data[offset]     = (byte)(ExtensionProfile >> 8);
            data[offset + 1] = (byte)(ExtensionProfile & 0xFF);
            data[offset + 2] = (byte)(wordLength >> 8);
            data[offset + 3] = (byte)(wordLength & 0xFF);
            offset += 4;
            Array.Copy(ExtensionData!, 0, data, offset, ExtensionData!.Length);
            // padding bytes are already zero from array initialisation
            offset += paddedExtLength;
        }

        // Payload
        if (Payload.Length > 0)
            Array.Copy(Payload, 0, data, offset, Payload.Length);

        return data;
    }
    
           
    /// <summary>
    /// Calculate expected sequence number difference (handles wraparound)
    /// </summary>
    public static int SequenceDifference(ushort seq1, ushort seq2)
    {
        // Handle 16-bit wraparound
        int diff = seq1 - seq2;
            
        if (diff < -32768)
            diff += 65536;
        else if (diff > 32768)
            diff -= 65536;
            
        return diff;
    }
        
    /// <summary>
    /// Calculate timestamp difference (handles wraparound)
    /// </summary>
    public static long TimestampDifference(uint ts1, uint ts2)
    {
        // Handle 32-bit wraparound
        long diff = (long)ts1 - (long)ts2;
            
        if (diff < -2147483648L)
            diff += 4294967296L;
        else if (diff > 2147483648L)
            diff -= 4294967296L;
            
        return diff;
    }
        
    public override string ToString()
    {
        return $"RTP[Seq={SequenceNumber}, TS={Timestamp}, SSRC=0x{Ssrc:X8}, PT={PayloadType}, Payload={Payload.Length}b]";
    }
}