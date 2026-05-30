namespace OpenFreqServer;

/// <summary>
/// Audio relay surface consumed by the signaling layer and stats. Extracted so the
/// signaling server can be exercised in integration tests without binding a real UDP
/// port or running the audio receive loop.
/// </summary>
public interface IAudioStreamServer
{
    /// <summary>Register an audio session for a client; returns the shared UDP port.</summary>
    int CreateAudioSession(string clientId);

    /// <summary>Last time an RTP packet was received from the client, or null if no session.</summary>
    DateTime? GetLastRtpReceived(string clientId);

    /// <summary>Tear down the client's audio session and associated routing state.</summary>
    void RemoveSession(string clientId);

    /// <summary>Stop the relay and release the UDP socket.</summary>
    void Stop();
}
