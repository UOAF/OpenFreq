using Microsoft.Extensions.Logging;
using OpenFreqClient.Services;

namespace OpenFreq.Client.Tests;

/// <summary>
/// Tests for how <see cref="OpenFreqClient.Services.OpenFreqService"/> starts and stops our own
/// transmissions. Starting one opens a BASS recording device, so only the paths that stop short of
/// the mic are covered here; the bookkeeping behind the rest is in <see cref="ActiveTransmissionsTests"/>.
/// </summary>
public class OpenFreqServiceTransmissionTests
{
    private const int Freq = 251_000;

    [Fact]
    public async Task StartTransmission_SlotNotTunedToFrequency_DoesNothing()
    {
        var h = new ServiceHarness();
        await h.InitializeAuthenticatedAsync();
        await h.Service.JoinFrequencyAsync(Freq, Guid.NewGuid(), ServiceHarness.NewRadioStation());

        // Another card on the same frequency that never joined it.
        await h.Service.StartTransmissionAsync(Freq, Guid.NewGuid());

        await h.Client.DidNotReceiveWithAnyArgs().StartTransmissionAsync(default, default);
        h.Playback.DidNotReceiveWithAnyArgs().SetTransmittingFrequencies(default!);
    }

    /// <summary>
    /// A press while the client reconnects must not key the slot. A key recorded then would send audio with no
    /// start once the link came back.
    /// </summary>
    [Fact]
    public async Task StartTransmission_WhileNotConnected_KeysNothing()
    {
        var logger = new CapturingLogger<OpenFreqService>();
        var h = new ServiceHarness(logger);
        await h.InitializeAuthenticatedAsync();
        var slot = Guid.NewGuid();
        await h.Service.JoinFrequencyAsync(Freq, slot, ServiceHarness.NewRadioStation());

        // A transparent reconnect keeps the slot tuned, and the client is briefly authenticated but not connected.
        h.Client.IsConnected.Returns(false);
        await h.Service.StartTransmissionAsync(Freq, slot);
        await h.Service.StopTransmissionAsync(slot);

        h.Client.DidNotReceive().MarkTransmitStartTime();
        await h.Client.DidNotReceiveWithAnyArgs().StartTransmissionAsync(default, default);
        await h.Client.DidNotReceiveWithAnyArgs().StopTransmissionAsync(default, default);
        h.Playback.DidNotReceiveWithAnyArgs().SetTransmittingFrequencies(default!);
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("recording device"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("not connected"));
    }

    [Fact]
    public async Task StopAndLeave_SlotNotTransmitting_SendNoStops()
    {
        var h = new ServiceHarness();
        await h.InitializeAuthenticatedAsync();
        var slot = Guid.NewGuid();
        await h.Service.JoinFrequencyAsync(Freq, slot, ServiceHarness.NewRadioStation());

        await h.Service.StopTransmissionAsync(slot);
        await h.Service.LeaveFrequencyAsync(Freq, slot);

        await h.Client.DidNotReceiveWithAnyArgs().StopTransmissionAsync(default, default);
        h.Playback.DidNotReceiveWithAnyArgs().SetTransmittingFrequencies(default!);
    }
}
