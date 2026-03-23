using System.Diagnostics;
using Concentus.Structs;
using Microsoft.Extensions.Logging;

namespace OpenFreq.Common;

/// <summary>
/// Encapsulates all state belonging to a single RTP synchronization source (SSRC).
/// Each concurrent talker gets an independent context so that jitter buffering,
/// Opus decoder state, and loss-concealment tracking never cross-contaminate
/// between streams.
/// </summary>
public sealed class RtpSourceContext : IDisposable
{
    public uint Ssrc { get; }
    
    public RtpJitterBuffer JitterBuffer { get; }
    public OpusDecoder OpusDecoder { get; }
    
    // Loss detection state
    public ushort LastSequenceReceived { get; set; }
    public bool FirstPacketReceived { get; set; }
    
    // Last valid metadata from this source, used to reconstruct concealment packets
    public AudioPacketMetadata? LastValidMetadata { get; set; }
    
    // Used by the pool to prune sources that have gone silent
    public long LastActivityTicks { get; set; }

    public RtpSourceContext(uint ssrc, ILoggerFactory loggerFactory, bool opusEnabled, int initialBufferMs)
    {
        Ssrc = ssrc;
        LastActivityTicks = Stopwatch.GetTimestamp();

        JitterBuffer = new RtpJitterBuffer(loggerFactory.CreateLogger<RtpJitterBuffer>());
        JitterBuffer.SetTargetBufferSize(initialBufferMs);

        #pragma warning disable CS0618 // Using the new factory method will not work on Linux
        OpusDecoder = new OpusDecoder(OpenFreqRtcClient.SAMPLE_RATE, 1);
        #pragma warning restore CS0618
    }

    public void Dispose()
    {
        OpusDecoder?.Dispose();
    }
}