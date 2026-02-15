using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Concentus.Structs;
using Microsoft.Extensions.Logging;
using OpenFreq.Common.Rtp;

namespace OpenFreq.Common;

/// <summary>
/// RTP audio receiver with adaptive jitter buffering.
/// Handles network layer: RTP parsing, jitter buffer, packet reordering, Opus decoding.
/// Outputs clean, ordered PCM audio ready for RadioPlayback.
/// </summary>
public class RtpAudioReceiver : IDisposable
{
    public class AudioReceivedEventArgs : EventArgs
    {
        public byte[] AudioData { get; set; } = Array.Empty<byte>();
        public required AudioPacketMetadata Metadata { get; set; }
    }

    public event EventHandler<AudioReceivedEventArgs>? AudioReceived;
    public event EventHandler<string>? ErrorOccurred;

    private readonly ILogger<RtpAudioReceiver> _logger;
    private readonly UdpClient _udpClient;
    private readonly RtpJitterBuffer _jitterBuffer;
    private readonly OpusDecoder? _opusDecoder;
    private readonly bool _opusEnabled;
    private readonly CancellationTokenSource _cts = new();
    private readonly Timer _playoutTimer;
    private readonly ILoggerFactory _loggerFactory;
    
    // Track expected sequence for loss detection
    private ushort _lastSequenceReceived;
    private bool _firstPacketReceived;
    
    // Track last valid metadata for PLC frequency reconstruction
    private AudioPacketMetadata? _lastValidMetadata = null;
    
    // FEC statistics
    private int _fecRecoveries = 0;
    private int _plcRecoveries = 0;
    
    // Frame size must be exact for PLC/FEC to maintain proper decoder state
    // Per Concentus docs: "frame_size needs to be exactly the duration of the audio that is missing,
    // otherwise the decoder will not be in an optimal state to decode the next incoming packet"
    private const int OPUS_FRAME_SAMPLES = 960; // 20ms at 48kHz (matches sender)
    private const int PLAYOUT_INTERVAL_MS = 20; // Check for ready packets every 20ms
        
    /// <summary>
    /// Create RTP audio receiver with adaptive jitter buffer
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="udpClient">Existing UDP client</param>
    /// <param name="opusEnabled">Whether to decode Opus (true) or expect raw PCM (false)</param>
    /// <param name="initialBufferMs">Initial jitter buffer size in milliseconds (will adapt)</param>
    public RtpAudioReceiver(ILoggerFactory loggerFactory, UdpClient udpClient, bool opusEnabled = true, int initialBufferMs = 150)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<RtpAudioReceiver>();
        _opusEnabled = opusEnabled;

        if (_opusEnabled)
        {
            #pragma warning disable CS0618 // Using the new factory method will not work in Linux!
            _opusDecoder =  new OpusDecoder(OpenFreqRtcClient.SAMPLE_RATE, OpenFreqRtcClient.CHANNELS) as OpusDecoder;
            #pragma warning restore CS0618 // Type or member is obsolete
        }

        // Create jitter buffer
        _jitterBuffer = new RtpJitterBuffer(loggerFactory.CreateLogger<RtpJitterBuffer>());
        _jitterBuffer.SetTargetBufferSize(initialBufferMs);

        // Reuse UDP Client
        _udpClient = udpClient;
        var port = (_udpClient.Client.LocalEndPoint as IPEndPoint)!.Port;
        _logger.LogInformation("Started on port {Port}", port);
        _logger.LogInformation("  Opus: {OpusEnabled}", _opusEnabled);
        _logger.LogInformation("  Initial buffer: {BufferMs}ms (adaptive)", initialBufferMs);

        // Start receiving task
        Task.Run(() => ReceiveLoop(), _cts.Token);
            
        // Start playout timer (pulls packets from jitter buffer)
        _playoutTimer = new Timer(PlayoutTimerCallback, null, 0, PLAYOUT_INTERVAL_MS);

        if (!_logger.IsEnabled(LogLevel.Debug))
        {
            Task.Run(() =>
            {
                while (true)
                {
                    var stats = GetStatistics();
                    _logger.LogDebug("Network Stats");
                    _logger.LogDebug("  Packets: received={Received}, lost={Lost} ({LossPercent:F1}%)",
                        stats.received, stats.lost, stats.lossPercent);
                    _logger.LogDebug("  Jitter: {JitterMs:F1}ms", stats.jitterMs);
                    _logger.LogDebug("  Buffer size: {BufferMs:F0}ms (adaptive)", stats.bufferMs);
                    _logger.LogDebug("  Buffered packets: {Buffered}", stats.buffered);
                    Task.Delay(5000).Wait();
                }
            });
        }
    }

    /// <summary>
    /// UDP receive loop - receives packets and adds to jitter buffer
    /// </summary>
    private async Task ReceiveLoop()
    {
        _logger.LogInformation("Receive loop started");
            
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync(_cts.Token);
                ProcessIncomingPacket(result.Buffer);
            }
            catch (OperationCanceledException)
            {
                break; // Expected during shutdown
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Receive error: {ex.Message}");
            }
        }
            
        _logger.LogInformation("Receive loop stopped");
    }

    /// <summary>
    /// Process incoming UDP packet
    /// </summary>
    private void ProcessIncomingPacket(byte[] data)
    {
        try
        {
            // Parse RTP packet
            var rtpPacket = RtpPacket.Parse(data);
            if (rtpPacket == null)
            {
                _logger.LogWarning("Invalid RTP packet");
                return;
            }

            // Add to jitter buffer (handles reordering, timing)
            _jitterBuffer.AddPacket(rtpPacket);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Packet processing error: {ex.Message}");
        }
    }

    /// <summary>
    /// Playout timer callback - pulls ready packets from jitter buffer
    /// Rate-limited: processes one packet per 20ms tick to prevent bursts
    /// </summary>
    private void PlayoutTimerCallback(object? state)
    {
        try
        {
            // Pull one ready packet per timer tick (prevents bursts that cause underruns)
            if (_jitterBuffer.GetNextPacket() is { } packet)
            {
                // Detect lost packets and generate concealment audio (FEC or PLC)
                if (_firstPacketReceived)
                {
                    ushort expectedSeq = (ushort)(_lastSequenceReceived + 1);
                    
                    // Check for gap in sequence numbers
                    if (packet.SequenceNumber != expectedSeq)
                    {
                        int gap = RtpPacket.SequenceDifference(packet.SequenceNumber, expectedSeq);
                        
                        if (gap > 0 && gap < 100) // Sanity check - only handle reasonable gaps
                        {
                            _logger.LogDebug("Detected {Gap} lost packets (seq {Start} to {End}), generating concealment", 
                                gap, expectedSeq, packet.SequenceNumber - 1);
                            
                            // Generate concealment for each lost packet
                            // The first lost packet can use FEC from current packet
                            // Subsequent lost packets must use PLC (we only have one "next" packet)
                            for (int i = 0; i < gap; i++)
                            {
                                ushort lostSeq = (ushort)(expectedSeq + i);
                                
                                // Only the first lost packet gets FEC attempt (using current packet)
                                // This is because current packet contains FEC for the previous (first lost) packet
                                if (i == 0)
                                {
                                    GenerateConcealmentAudio(lostSeq, packet);  // Pass current packet for FEC
                                }
                                else
                                {
                                    GenerateConcealmentAudio(lostSeq, null);    // Subsequent: PLC only
                                }
                            }
                        }
                    }
                }
                else
                {
                    _firstPacketReceived = true;
                }
                
                _lastSequenceReceived = packet.SequenceNumber;
                ProcessReadyPacket(packet);
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Playout error: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Generate concealment audio for a lost packet (FEC preferred, PLC fallback)
    /// </summary>
    /// <param name="lostSequence">Sequence number of the lost packet</param>
    /// <param name="nextPacket">The packet AFTER the lost one (contains FEC data for lost packet)</param>
    private void GenerateConcealmentAudio(ushort lostSequence, RtpPacket? nextPacket)
    {
        if (!_opusEnabled || _opusDecoder == null)
            return;
            
        try
        {
            byte[] concealmentAudio;
            bool usedFec = false;
            
            // Try FEC if we have the next packet
            // According to Concentus docs: "you will actually decode this packet twice, first with decode_fec TRUE and then again with FALSE"
            if (nextPacket != null)
            {
                var nextPacketOpusData = ExtractOpusDataFromPacket(nextPacket);
                
                _logger.LogDebug("Loss recovery for seq {LostSeq}: have nextPacket (seq {NextSeq}), OpusData={Bytes} bytes", 
                    lostSequence, nextPacket.SequenceNumber, nextPacketOpusData?.Length ?? 0);
                
                if (nextPacketOpusData != null && nextPacketOpusData.Length > 0)
                {
                    // Decode with decode_fec=TRUE to extract FEC data for the LOST packet
                    // The nextPacket will be decoded AGAIN with decode_fec=FALSE in ProcessReadyPacket()
                    concealmentAudio = DecodeOpus(nextPacketOpusData, isLost: false, decodeFec: true);
                    
                    if (concealmentAudio.Length > 0)
                    {
                        usedFec = true;
                        _fecRecoveries++;
                        _logger.LogInformation("✓ FEC: Recovered seq {LostSeq} using FEC from seq {NextSeq} ({Bytes} bytes)", 
                            lostSequence, nextPacket.SequenceNumber, concealmentAudio.Length);
                    }
                    else
                    {
                        // FEC decode failed (no FEC data in packet, or decode error)
                        _logger.LogDebug("  FEC decode returned 0 bytes, falling back to PLC");
                        concealmentAudio = DecodeOpus([], isLost: true);
                        _plcRecoveries++;
                    }
                }
                else
                {
                    // Couldn't extract Opus data
                    _logger.LogDebug("  Could not extract Opus data, using PLC");
                    concealmentAudio = DecodeOpus([], isLost: true);
                    _plcRecoveries++;
                }
            }
            else
            {
                // No next packet available - use PLC
                _logger.LogDebug("Loss recovery for seq {LostSeq}: no nextPacket, using PLC", lostSequence);
                concealmentAudio = DecodeOpus([], isLost: true);
                _plcRecoveries++;
            }
            
            if (concealmentAudio.Length > 0)
            {
                // Reconstruct frequency list from last valid packet
                var frequencies = new List<FrequencyTransmission>();
                
                if (_lastValidMetadata?.Frequencies != null)
                {
                    foreach (var freq in _lastValidMetadata.Frequencies)
                    {
                        frequencies.Add(new FrequencyTransmission(
                            khz: freq.Khz,
                            txPowerWatts: freq.TxPowerWatts,
                            position: freq.Position,
                            in3d: freq.In3d,
                            beginMarker: false,
                            endMarker: false
                        ));
                    }
                }
                
                var metadata = new AudioPacketMetadata
                {
                    ClientId = _lastValidMetadata?.ClientId ?? (usedFec ? "FEC" : "PLC"),
                    Frequencies = frequencies
                };
                
                AudioReceived?.Invoke(this, new AudioReceivedEventArgs
                {
                    AudioData = concealmentAudio,
                    Metadata = metadata
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate concealment audio for seq {LostSeq}", lostSequence);
        }
    }

    /// <summary>
    /// Process packet that's ready for playout
    /// </summary>
    private void ProcessReadyPacket(RtpPacket packet)
    {
        try
        {
            // Parse metadata from payload
            // Format: [2 bytes metadata len][JSON metadata][audio data]
            if (packet.Payload.Length < 2)
            {
                _logger.LogWarning("Payload too small");
                return;
            }

            var metadataLength = (ushort)((packet.Payload[0] << 8) | packet.Payload[1]);
                
            if (metadataLength + 2 > packet.Payload.Length)
            {
                _logger.LogWarning("Invalid metadata length: {MetadataLength}", metadataLength);
                return;
            }

            // Extract metadata JSON
            var metadataJson = Encoding.UTF8.GetString(packet.Payload, 2, metadataLength);
            var metadata = JsonSerializer.Deserialize(metadataJson, OpenFreqJsonContext.Default.AudioPacketMetadata);

            if (metadata == null)
            {
                _logger.LogWarning("Failed to parse metadata");
                return;
            }
            
            // Save metadata for PLC frequency reconstruction
            _lastValidMetadata = metadata;

            // Extract audio data
            var audioDataStart = 2 + metadataLength;
            var audioDataLength = packet.Payload.Length - audioDataStart;
            var audioData = new byte[audioDataLength];
            Array.Copy(packet.Payload, audioDataStart, audioData, 0, audioDataLength);

            // Decode audio if Opus is enabled
            byte[] decodedAudio;
            if (_opusEnabled && _opusDecoder != null)
            {
                decodedAudio = DecodeOpus(audioData, isLost: false, decodeFec: false);
                if (decodedAudio.Length == 0)
                    return; // Decode failed
            }
            else
            {
                // Raw PCM - use as-is
                decodedAudio = audioData;
            }

            // Fire event with clean audio
            AudioReceived?.Invoke(this, new AudioReceivedEventArgs
            {
                AudioData = decodedAudio,
                Metadata = metadata
            });
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Ready packet processing error: {ex.Message}");
        }
    }

    /// <summary>
    /// Decode Opus audio with PLC support
    /// </summary>
    /// <param name="opusData">Opus-encoded data, or empty array for PLC</param>
    /// <param name="isLost">True if this is a lost packet requiring PLC</param>
    /// <param name="decodeFec">True to decode FEC data from this packet (for previous lost packet)</param>
    private byte[] DecodeOpus(byte[] opusData, bool isLost = false, bool decodeFec = false)
    {
        if (_opusDecoder == null)
            return [];

        try
        {
            const int MAX_OPUS_FRAME_SAMPLES = 5760; // 120ms at 48kHz
            short[] pcmSamples = new short[MAX_OPUS_FRAME_SAMPLES];

            int? samplesDecoded;
            
            if (isLost)
            {
                // Use Opus PLC for lost packets
                samplesDecoded = _opusDecoder?.Decode(
                    Array.Empty<byte>(),  // Empty array triggers PLC
                    0, 
                    0, 
                    pcmSamples, 
                    0, 
                    OPUS_FRAME_SAMPLES,  // Must match exact frame duration for proper decoder state
                    false
                );
                
                if (samplesDecoded > 0)
                {
                    _logger.LogDebug("Generated PLC audio: {Samples} samples", samplesDecoded);
                }
            }
            else if (decodeFec)
            {
                // Decode FEC data from this packet
                samplesDecoded = _opusDecoder?.Decode(
                    opusData, 
                    0, 
                    opusData.Length, 
                    pcmSamples, 
                    0, 
                    OPUS_FRAME_SAMPLES,
                    true 
                );
                
                if (samplesDecoded > 0)
                {
                    _logger.LogDebug("FEC decode successful: {Samples} samples from {Bytes} bytes", 
                        samplesDecoded, opusData.Length);
                }
            }
            else
            {
                // Normal decode
                samplesDecoded = _opusDecoder?.Decode(
                    opusData, 
                    0, 
                    opusData.Length, 
                    pcmSamples, 
                    0, 
                    MAX_OPUS_FRAME_SAMPLES, 
                    false
                );
            }

            if (samplesDecoded is null or <= 0)
            {
                _logger.LogWarning("Opus decode failed for {ByteCount} bytes (isLost={IsLost}, decodeFec={DecodeFec})", 
                    opusData.Length, isLost, decodeFec);
                return [];
            }

            // Convert to byte array (16-bit PCM)
            var decodedAudio = new byte[samplesDecoded.Value * 2];
            Buffer.BlockCopy(pcmSamples, 0, decodedAudio, 0, decodedAudio.Length);
                
            return decodedAudio;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opus decode exception (isLost={IsLost}, decodeFec={DecodeFec})", isLost, decodeFec);
            return [];
        }
    }
    
    /// <summary>
    /// Extract Opus-encoded audio data from RTP packet payload
    /// Payload format: [2 bytes metadata len][JSON metadata][audio data]
    /// </summary>
    private byte[]? ExtractOpusDataFromPacket(RtpPacket packet)
    {
        try
        {
            if (packet.Payload.Length < 2)
                return null;

            var metadataLength = (ushort)((packet.Payload[0] << 8) | packet.Payload[1]);
            
            if (metadataLength + 2 > packet.Payload.Length)
                return null;

            // Extract audio data (after metadata)
            var audioDataStart = 2 + metadataLength;
            var audioDataLength = packet.Payload.Length - audioDataStart;
            
            if (audioDataLength <= 0)
                return null;
            
            var audioData = new byte[audioDataLength];
            Array.Copy(packet.Payload, audioDataStart, audioData, 0, audioDataLength);
            
            return audioData;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Get receiver statistics
    /// </summary>
    public (int received, int lost, int late, int duplicate, int played, 
        double lossPercent, double jitterMs, double bufferMs, int buffered) GetStatistics()
    {
        return _jitterBuffer.GetStatistics();
    }

    /// <summary>
    /// Print detailed statistics
    /// </summary>
    public void PrintStatistics()
    {
        var stats = GetStatistics();
        _logger.LogInformation("=== RTP Receiver Statistics ===");
        _logger.LogInformation("  Packets received: {Received}", stats.received);
        _logger.LogInformation("  Packets lost: {Lost} ({LossPercent:F2}%)", stats.lost, stats.lossPercent);
        _logger.LogInformation("  Packets late: {Late}", stats.late);
        _logger.LogInformation("  Packets duplicate: {Duplicate}", stats.duplicate);
        _logger.LogInformation("  Packets played: {Played}", stats.played);
        _logger.LogInformation("  Loss Recovery:");
        _logger.LogInformation("    FEC recoveries: {FecCount}", _fecRecoveries);
        _logger.LogInformation("    PLC recoveries: {PlcCount}", _plcRecoveries);
        
        if (_fecRecoveries + _plcRecoveries > 0)
        {
            var fecPercent = 100.0 * _fecRecoveries / (_fecRecoveries + _plcRecoveries);
            _logger.LogInformation("    FEC success rate: {FecRate:F1}%", fecPercent);
        }
        
        _logger.LogInformation("  Measured jitter: {JitterMs:F1}ms", stats.jitterMs);
        _logger.LogInformation("  Buffer size: {BufferMs:F0}ms (adaptive)", stats.bufferMs);
        _logger.LogInformation("  Currently buffered: {Buffered} packets", stats.buffered);
        _logger.LogInformation("================================");
    }

    /// <summary>
    /// Set jitter buffer size manually (overrides adaptation temporarily)
    /// </summary>
    public void SetJitterBufferSize(int milliseconds)
    {
        _jitterBuffer.SetTargetBufferSize(milliseconds);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _playoutTimer?.Dispose();
        _opusDecoder?.Dispose();
        PrintStatistics();
        _logger.LogInformation("Disposed");
    }
}