using OpenFreq.Common;
using OpenFreqClient.Services.Interfaces;

namespace OpenFreq.Client.Tests;

/// <summary>
/// Tests for <see cref="OpenFreqClient.Services.OpenFreqService"/>
/// </summary>
public class OpenFreqServiceTests
{
    private const int Freq = 251_000;

    [Fact]
    public async Task Initialize_CreatesAndInitializesPlayback()
    {
        var h = new ServiceHarness();

        await h.InitializeAsync();

        h.Playback.Received(1).Initialize();
        Assert.Equal(IOpenFreqService.OpenFreqStatus.Disconnected, h.Service.Status);
    }

    [Fact]
    public async Task ConnectAsync_BeforeInitialize_Throws()
    {
        var h = new ServiceHarness();

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.ConnectAsync());
    }

    [Fact]
    public async Task ConnectAsync_SetsConnecting_AndCallsClient()
    {
        var h = new ServiceHarness();
        await h.InitializeAsync();

        await h.Service.ConnectAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(IOpenFreqService.OpenFreqStatus.Connecting, h.Service.Status);
        await h.Client.Received(1).ConnectAsync(Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task ClientConnectionState_Authenticated_UpdatesStatusAndReRaises()
    {
        var h = new ServiceHarness();
        await h.InitializeAsync();

        ConnectionStateChangedEventArgs? seen = null;
        h.Service.ConnectionStateChanged += (_, e) => seen = e;

        h.Client.ConnectionStateChanged +=
            Raise.EventWith(new ConnectionStateChangedEventArgs(ConnectionState.Authenticated));

        Assert.Equal(IOpenFreqService.OpenFreqStatus.Authenticated, h.Service.Status);
        Assert.NotNull(seen);
        Assert.Equal(ConnectionState.Authenticated, seen!.State);
    }

    [Fact]
    public async Task ClientConnectionState_Disconnected_ClearsTunedSlots()
    {
        var h = new ServiceHarness();
        await h.InitializeAuthenticatedAsync();
        var slot = Guid.NewGuid();
        await h.Service.JoinFrequencyAsync(Freq, slot, ServiceHarness.NewRadioStation());
        Assert.True(h.Service.IsFrequencyJoined(Freq, slot));

        h.Client.ConnectionStateChanged +=
            Raise.EventWith(new ConnectionStateChangedEventArgs(ConnectionState.Disconnected));

        Assert.False(h.Service.IsFrequencyJoined(Freq, slot));
    }

    [Fact]
    public async Task JoinFrequency_Authenticated_TunesPlaybackAndJoinsServer()
    {
        var h = new ServiceHarness();
        await h.InitializeAuthenticatedAsync();
        var slot = Guid.NewGuid();

        await h.Service.JoinFrequencyAsync(Freq, slot, ServiceHarness.NewRadioStation());

        h.Playback.Received(1).TuneFrequency(Freq, slot);
        await h.Client.Received(1).JoinFrequencyAsync(Freq);
        Assert.True(h.Service.IsFrequencyJoined(Freq, slot));
    }

    [Fact]
    public async Task JoinFrequency_NotAuthenticated_IsNoOp()
    {
        var h = new ServiceHarness();
        await h.InitializeAsync(); // not authenticated
        var slot = Guid.NewGuid();

        await h.Service.JoinFrequencyAsync(Freq, slot, ServiceHarness.NewRadioStation());

        Assert.False(h.Service.IsFrequencyJoined(Freq, slot));
        await h.Client.DidNotReceive().JoinFrequencyAsync(Arg.Any<int>());
        h.Playback.DidNotReceive().TuneFrequency(Arg.Any<int>(), Arg.Any<Guid>());
    }

    [Fact]
    public async Task LeaveFrequency_LastSlot_UntunesAndLeavesServer()
    {
        var h = new ServiceHarness();
        await h.InitializeAuthenticatedAsync();
        var slot = Guid.NewGuid();
        await h.Service.JoinFrequencyAsync(Freq, slot, ServiceHarness.NewRadioStation());

        await h.Service.LeaveFrequencyAsync(Freq, slot);

        h.Playback.Received(1).UntuneFrequency(Freq, slot);
        await h.Client.Received(1).LeaveFrequencyAsync(Freq);
        Assert.False(h.Service.IsFrequencyJoined(Freq, slot));
    }

    [Fact]
    public async Task ClientFrequencyJoined_ReRaisesEvent()
    {
        var h = new ServiceHarness();
        await h.InitializeAsync();

        FrequencyJoinedEventArgs? seen = null;
        h.Service.FrequencyJoined += (_, e) => seen = e;

        h.Client.FrequencyJoined +=
            Raise.EventWith(new FrequencyJoinedEventArgs(Freq, new List<ChannelStateMessage.Peer>()));

        Assert.NotNull(seen);
        Assert.Equal(Freq, seen!.FrequencyKhz);
    }

    [Fact]
    public async Task ClientPeerJoined_StartsPushStreamAndReRaises()
    {
        var h = new ServiceHarness();
        await h.InitializeAsync();

        PeerEventArgs? seen = null;
        h.Service.PeerJoined += (_, e) => seen = e;

        h.Client.PeerJoined += Raise.EventWith(new PeerEventArgs("peer1", "Bob", Freq));

        Assert.NotNull(seen);
        Assert.Equal("peer1", seen!.PeerId);
        h.Playback.Received(1).StartPushStream(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<OpenFreqAudio.AudioParams>());
    }

    [Fact]
    public async Task ClientPeerLeft_ReRaises()
    {
        var h = new ServiceHarness();
        await h.InitializeAsync();

        PeerEventArgs? seen = null;
        h.Service.PeerLeft += (_, e) => seen = e;

        h.Client.PeerLeft += Raise.EventWith(new PeerEventArgs("peer1", "Bob", Freq));

        Assert.NotNull(seen);
        Assert.Equal("peer1", seen!.PeerId);
    }

    [Fact]
    public async Task ClientAllPeersStatus_ReRaises()
    {
        var h = new ServiceHarness();
        await h.InitializeAsync();

        AllPeersStatusEventArgs? seen = null;
        h.Service.AllPeersStatusChanged += (_, e) => seen = e;

        h.Client.AllPeersStatusUpdateReceived +=
            Raise.EventWith(new AllPeersStatusEventArgs(new SortedDictionary<int, List<PeerData>>()));

        Assert.NotNull(seen);
    }

    [Fact]
    public async Task Setters_ForwardToPlayback()
    {
        var h = new ServiceHarness();
        await h.InitializeAsync();
        var slot = Guid.NewGuid();

        h.Service.SetVolume(Freq, slot, 0.5f);
        h.Service.SetPan(Freq, slot, 25);
        h.Service.SetSquelch(Freq, slot, isSquelchClosed: true);

        h.Playback.Received(1).SetFrequencyVolume(Freq, slot, 0.5f);
        h.Playback.Received(1).SetFrequencyPan(Freq, slot, 25);
        h.Playback.Received(1).SetSquelchLevel(Freq, slot, 1f);
    }

    [Fact]
    public async Task StartRecording_DeviceSink_StartsMonitorAndRaisesEvent()
    {
        var h = new ServiceHarness();
        await h.InitializeAsync();
        h.Service.Sink = IOpenFreqService.CaptureSink.Device;
        h.Service.MonitorDeviceIndex = 3;
        h.Playback.IsMonitoring.Returns(true); // monitor "started" successfully

        bool? recordingState = null;
        h.Service.RecordingStateChanged += (_, on) => recordingState = on;

        h.Service.StartRecording();

        h.Playback.Received(1).StartMonitor(3);
        Assert.True(recordingState);
    }

    [Fact]
    public async Task StopRecording_WhenCapturing_StopsAndRaisesEvent()
    {
        var h = new ServiceHarness();
        await h.InitializeAsync();
        h.Playback.IsCapturing.Returns(true);

        bool? recordingState = null;
        h.Service.RecordingStateChanged += (_, on) => recordingState = on;

        h.Service.StopRecording();

        h.Playback.Received(1).StopRecording();
        Assert.False(recordingState);
    }
}
