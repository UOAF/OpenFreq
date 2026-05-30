using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using OpenFreqServer;

namespace OpenFreq.Server.Tests.Integration;

/// <summary>
/// Spins up the real <see cref="AudioStreamServer"/> bound to a free loopback UDP port and runs
/// its receive loop, backed by a real <see cref="FrequencyChannelManager"/> and client table.
/// Tests drive it over real UDP via <see cref="TestAudioClient"/> — only the signaling layer is
/// absent (sessions are wired up directly). One harness per test for isolation.
/// </summary>
public sealed class AudioServerHarness : IDisposable
{
    public int Port { get; }
    public FrequencyChannelManager Channels { get; } = new();
    public ConcurrentDictionary<string, ClientSession> Clients { get; } = new();
    public AudioStreamServer Server { get; }

    private AudioServerHarness()
    {
        Port = FindFreeUdpPort();
        // Use a factory that enables all log levels so IsEnabled(...)-gated paths are exercised.
        Server = new AudioStreamServer(Channels, Clients, EnabledLoggerFactory.Instance, Port);
    }

    public static AudioServerHarness Start() => new();

    /// <summary>
    /// Register an authenticated client: creates its session/channel membership exactly as the
    /// signaling layer would, then returns a connected UDP client bound to it.
    /// </summary>
    public TestAudioClient AddClient(string id, params int[] frequencies) =>
        AddClient(id, authenticated: true, frequencies);

    public TestAudioClient AddClient(string id, bool authenticated, params int[] frequencies)
    {
        var session = new ClientSession(id, id, null!, "127.0.0.1") { IsAuthenticated = authenticated };
        foreach (var khz in frequencies)
        {
            session.CurrentFrequencies[khz] = ClientSession.FrequencyClientStatus.Receiving;
            Channels.JoinChannel(khz, id, id);
        }

        Clients[id] = session;
        Server.CreateAudioSession(id);

        return new TestAudioClient(Port, id);
    }

    public void Dispose() => Server.Stop();

    /// <summary>Bind an ephemeral UDP socket to discover a free port, then release it.</summary>
    private static int FindFreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }
}
