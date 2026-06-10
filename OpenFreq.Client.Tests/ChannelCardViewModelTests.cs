using CommunityToolkit.Mvvm.Messaging;
using OpenFreqClient.Services;
using OpenFreqClient.Services.Interfaces;
using OpenFreqClient.ViewModels;

namespace OpenFreq.Client.Tests;

/// <summary>
/// Tests for <see cref="ChannelCardViewModel"/> (without actually displaying it).
/// </summary>
public class ChannelCardViewModelTests
{
    private const int Freq = 251_000;

    private static ChannelCardViewModel CreateVm(int frequencyKhz = Freq)
        => new(
            Substitute.For<IOpenFreqService>(),
            Substitute.For<IHotkeyService>(),
            name: "Ch1",
            frequencyKhz: frequencyKhz,
            isInEditMode: false,
            radioStationData: ServiceHarness.NewRadioStation(),
            parentLocationViewModel: null!,
            settings: null!);

    [Fact]
    public void SignalStrengthMessage_MatchingFrequency_UpdatesProperties()
    {
        var vm = CreateVm();

        WeakReferenceMessenger.Default.Send(
            new SignalStrengthTracker.SignalStrengthUpdateMessage(Freq, strengthPercent: 0.75f, snrDb: 12.5f));

        Assert.Equal(0.75, vm.SignalStrengthPercent, precision: 3);
        Assert.Equal(12.5, vm.SignalStrengthDbm, precision: 3);
    }

    [Fact]
    public void SignalStrengthMessage_DifferentFrequency_Ignored()
    {
        var vm = CreateVm(Freq);

        WeakReferenceMessenger.Default.Send(
            new SignalStrengthTracker.SignalStrengthUpdateMessage(Freq + 1000, strengthPercent: 0.9f, snrDb: 20f));

        Assert.Equal(0, vm.SignalStrengthPercent);
        Assert.Equal(0, vm.SignalStrengthDbm);
    }
}
