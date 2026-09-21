using System.Threading;
using FalconBmsDataService.Models;
using FalconBmsDataService.Services;
using FalconRadioService.Models;
using FalconRadioService.Services;
using Microsoft.Extensions.Logging;
using OpenFreq.Client.Models;
using OpenFreqClient.Models;
using OpenFreqClient.Services.Interfaces;
using OpenFreqClient.ViewModels;

namespace OpenFreq.Client.Tests;

/// <summary>
/// Covers the BMS radio slot switch. A ramp start walks each radio through several presets in under a
/// second while the power comes on, so these tests drive the same bursts the RCC loop sees there.
/// </summary>
public class BmsSlotSwitchTests
{
    private const int Uhf1 = 227_225;
    private const int Uhf2 = 350_225;
    private const int Uhf3 = 327_000;
    private const int Uhf4 = 275_800;
    private const int Uhf5 = 234_900;
    private const int GuardFreq = 243_000;

    [Fact]
    public async Task RampStorm_LeavesTheSlotOnTheLastFrequencyOnly()
    {
        var bms = new BmsRadios();
        bms.SetRadio(RadioType.Radio1, Uhf1, isOn: true);

        foreach (var (from, to) in new[] { (Uhf1, Uhf2), (Uhf2, Uhf3), (Uhf3, Uhf4), (Uhf4, Uhf5) })
            bms.Retune(RadioType.Radio1, from, to);

        await bms.Settles(() => bms.Joined(RadioType.Radio1).SequenceEqual([Uhf5]));

        Assert.Equal(Uhf5, bms.Card(RadioType.Radio1).FrequencyKhz);
        Assert.Empty(bms.Warnings);
    }

    [Fact]
    public async Task ASecondSwitch_WaitsForTheFirstToFinish()
    {
        var bms = new BmsRadios();
        bms.SetRadio(RadioType.Radio1, Uhf1, isOn: true);

        var firstJoinStarted = new TaskCompletionSource();
        var releaseFirstJoin = new TaskCompletionSource();
        bms.BlockJoin(Uhf2, firstJoinStarted, releaseFirstJoin);

        bms.Retune(RadioType.Radio1, Uhf1, Uhf2);
        await firstJoinStarted.Task.WaitAsync(WaitLimit);

        bms.Retune(RadioType.Radio1, Uhf2, Uhf3);
        await Task.Delay(100);

        // The second switch must not touch the slot while the first one is still joining.
        Assert.Equal(Uhf2, bms.Card(RadioType.Radio1).FrequencyKhz);

        releaseFirstJoin.SetResult();
        await bms.Settles(() => bms.Joined(RadioType.Radio1).SequenceEqual([Uhf3]));
        Assert.Equal(Uhf3, bms.Card(RadioType.Radio1).FrequencyKhz);
    }

    [Fact]
    public async Task PowerOn_WithoutAFrequencyChange_JoinsTheSlot()
    {
        var bms = new BmsRadios();
        bms.SetRadio(RadioType.Radio1, Uhf5, isOn: false);

        bms.PowerOn(RadioType.Radio1);

        await bms.Settles(() => bms.Joined(RadioType.Radio1).SequenceEqual([Uhf5]));
        Assert.Equal(Channel.ChannelConnectionStatus.Connected, bms.Card(RadioType.Radio1).ConnectionStatus);
    }

    [Fact]
    public async Task PowerOff_LeavesTheSlot()
    {
        var bms = new BmsRadios();
        bms.SetRadio(RadioType.Radio1, Uhf5, isOn: true);
        bms.PowerOn(RadioType.Radio1);
        await bms.Settles(() => bms.Joined(RadioType.Radio1).SequenceEqual([Uhf5]));

        bms.PowerOff(RadioType.Radio1);

        await bms.Settles(() => bms.Joined(RadioType.Radio1).Count == 0);
        Assert.Equal(Channel.ChannelConnectionStatus.Disconnected, bms.Card(RadioType.Radio1).ConnectionStatus);
        Assert.Empty(bms.Warnings);
    }

    [Fact]
    public async Task TwoRadiosOnOneFrequency_KeepSeparateCards()
    {
        var bms = new BmsRadios();
        bms.SetRadio(RadioType.Radio1, Uhf5, isOn: true);
        bms.SetRadio(RadioType.Radio2, Uhf5, isOn: true);

        bms.PowerOn(RadioType.Radio1);
        bms.PowerOn(RadioType.Radio2);

        await bms.Settles(() => bms.Joined(RadioType.Radio1).SequenceEqual([Uhf5]) &&
                                bms.Joined(RadioType.Radio2).SequenceEqual([Uhf5]));

        Assert.Equal(3, bms.Location.Channels.Count);
        Assert.NotEqual(bms.Card(RadioType.Radio1).Id, bms.Card(RadioType.Radio2).Id);
        Assert.Empty(bms.Warnings);
    }

    [Theory]
    [InlineData(IFalconRadioSharedMemoryService.BmsRadioOffFrequency)]
    [InlineData(IFalconRadioSharedMemoryService.BmsVhfRadioOffFrequency)]
    public async Task AParkedRadio_IsNeverJoined(int parkingFrequency)
    {
        var bms = new BmsRadios();
        bms.SetRadio(RadioType.Radio1, Uhf5, isOn: true);
        bms.PowerOn(RadioType.Radio1);
        await bms.Settles(() => bms.Joined(RadioType.Radio1).SequenceEqual([Uhf5]));

        // BMS parks a radio it has switched off, with the power flag still set.
        bms.Retune(RadioType.Radio1, Uhf5, parkingFrequency);

        await bms.Settles(() => bms.Joined(RadioType.Radio1).Count == 0);
        Assert.Equal(parkingFrequency, bms.Card(RadioType.Radio1).FrequencyKhz);
    }

    [Fact]
    public async Task AStaleJoin_IsClearedByTheNextSwitch()
    {
        var bms = new BmsRadios();
        bms.SetRadio(RadioType.Radio1, Uhf5, isOn: true);
        bms.SeedJoin(RadioType.Radio1, Uhf2);

        bms.PowerOn(RadioType.Radio1);

        await bms.Settles(() => bms.Joined(RadioType.Radio1).SequenceEqual([Uhf5]));
    }

    [Fact]
    public async Task AJoinThatDoesNotTake_IsReportedAsOutOfSync()
    {
        var bms = new BmsRadios();
        bms.SetRadio(RadioType.Radio1, Uhf5, isOn: true);
        bms.DropJoins();

        bms.PowerOn(RadioType.Radio1);

        await bms.Settles(() => bms.Warnings.Count > 0);
        Assert.Contains(bms.Warnings, w => w.Contains("Radio1 out of sync") && w.Contains($"{Uhf5}"));
    }

    [Fact]
    public async Task Guard_TracksItsOwnPowerSeparately()
    {
        var bms = new BmsRadios();
        bms.SetRadio(RadioType.Radio1, Uhf5, isOn: true);
        bms.SetRadio(RadioType.Guard, GuardFreq, isOn: false);

        bms.PowerOn(RadioType.Radio1);
        await bms.Settles(() => bms.Joined(RadioType.Radio1).SequenceEqual([Uhf5]));
        Assert.Empty(bms.Joined(RadioType.Guard));

        bms.PowerOn(RadioType.Guard);
        await bms.Settles(() => bms.Joined(RadioType.Guard).SequenceEqual([GuardFreq]));
        Assert.Equal([Uhf5], bms.Joined(RadioType.Radio1));
    }

    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A BMS location with one card per radio slot, wired to a service that models which frequencies
    /// each slot holds. Raises the same RCC events the shared memory service raises.
    /// </summary>
    private sealed class BmsRadios
    {
        private readonly IFalconRadioSharedMemoryService _rcc = Substitute.For<IFalconRadioSharedMemoryService>();
        private readonly CapturingLogger<ChannelCardListViewModel> _logger = new();
        private readonly Dictionary<RadioType, ChannelCardViewModel> _cards = new();
        private readonly Dictionary<RadioType, RadioChannel> _radios = new();
        private readonly HashSet<(int FreqKhz, Guid SlotId)> _joined = [];
        private readonly Lock _gate = new();

        private int _blockedJoinFrequency = -1;
        private TaskCompletionSource? _blockedJoinStarted;
        private TaskCompletionSource? _blockedJoinRelease;
        private bool _dropJoins;

        public LocationViewModel Location { get; }

        public BmsRadios()
        {
            var openFreq = VmFactory.OpenFreq();
            openFreq.IsAuthenticated.Returns(true);

            openFreq.JoinFrequencyAsync(Arg.Any<int>(), Arg.Any<Guid>(), Arg.Any<RadioStationData>())
                .Returns(async ci =>
                {
                    var frequencyKhz = ci.ArgAt<int>(0);
                    var slotId = ci.ArgAt<Guid>(1);

                    if (frequencyKhz == _blockedJoinFrequency)
                    {
                        _blockedJoinStarted?.TrySetResult();
                        await _blockedJoinRelease!.Task;
                    }

                    lock (_gate)
                    {
                        if (!_dropJoins) _joined.Add((frequencyKhz, slotId));
                    }
                });

            openFreq.LeaveFrequencyAsync(Arg.Any<int>(), Arg.Any<Guid>())
                .Returns(ci =>
                {
                    lock (_gate) _joined.Remove((ci.ArgAt<int>(0), ci.ArgAt<Guid>(1)));
                    return Task.CompletedTask;
                });

            openFreq.IsFrequencyJoined(Arg.Any<int>(), Arg.Any<Guid>())
                .Returns(ci =>
                {
                    lock (_gate) return _joined.Contains((ci.ArgAt<int>(0), ci.ArgAt<Guid>(1)));
                });

            openFreq.GetJoinedFrequencies(Arg.Any<Guid>())
                .Returns(IReadOnlyList<int> (ci) =>
                {
                    var slotId = ci.ArgAt<Guid>(0);
                    lock (_gate) return _joined.Where(j => j.SlotId == slotId).Select(j => j.FreqKhz).ToList();
                });

            _rcc.GetRadioChannel(Arg.Any<RadioType>())
                .Returns(ci =>
                {
                    lock (_gate) return _radios[ci.Arg<RadioType>()].Clone();
                });

            var hotkey = VmFactory.Hotkey();
            var falcon = Substitute.For<IFalconSharedMemoryService>();
            falcon.IsFlying.Returns(true);
            var vm = VmFactory.ChannelCardList(openFreq, hotkey, _rcc, falcon, _logger);
            vm.Settings.ModeIsGci = false;

            // CreateChannel adds the card through the UI dispatcher, which doesn't run in tests,
            // so the cards are built and added here the way ImportBmsRadioChannels builds them.
            Location = VmFactory.Location(RadioStationData.RadioStationType.BMS, openFreq);
            foreach (var type in Enum.GetValues<RadioType>())
            {
                _radios[type] = new RadioChannel(type) { Frequency = 0, IsOn = false };
                var card = new ChannelCardViewModel(hotkey, type.ToString(), 0, false,
                    Location.RadioStationData, Location, Location.Settings)
                {
                    BmsRadioType = type
                };
                _cards[type] = card;
                Location.Channels.Add(card);
            }

            vm.FalconLocation = Location;
        }

        public ChannelCardViewModel Card(RadioType type) => _cards[type];

        public IReadOnlyList<int> Joined(RadioType type)
        {
            var slotId = _cards[type].Id;
            lock (_gate) return _joined.Where(j => j.SlotId == slotId).Select(j => j.FreqKhz).Order().ToList();
        }

        public IReadOnlyList<string> Warnings =>
            _logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();

        public void SetRadio(RadioType type, int frequencyKhz, bool isOn)
        {
            lock (_gate)
            {
                _radios[type].Frequency = frequencyKhz;
                _radios[type].IsOn = isOn;
            }

            _cards[type].FrequencyKhz = frequencyKhz;
        }

        public void SeedJoin(RadioType type, int frequencyKhz)
        {
            lock (_gate) _joined.Add((frequencyKhz, _cards[type].Id));
        }

        public void BlockJoin(int frequencyKhz, TaskCompletionSource started, TaskCompletionSource release)
        {
            _blockedJoinFrequency = frequencyKhz;
            _blockedJoinStarted = started;
            _blockedJoinRelease = release;
        }

        public void DropJoins() => _dropJoins = true;

        public void Retune(RadioType type, int oldFrequencyKhz, int newFrequencyKhz)
        {
            lock (_gate) _radios[type].Frequency = newFrequencyKhz;
            _rcc.FrequencyChanged +=
                Raise.EventWith(new RadioFrequencyChangedEventArgs(type, oldFrequencyKhz, newFrequencyKhz));
        }

        public void PowerOn(RadioType type) => SetPower(type, true);

        public void PowerOff(RadioType type) => SetPower(type, false);

        private void SetPower(RadioType type, bool isOn)
        {
            lock (_gate) _radios[type].IsOn = isOn;
            _rcc.PowerChanged += Raise.EventWith(new RadioPowerChangedEventArgs(type, !isOn, isOn));
        }

        /// <summary>
        /// Waits for the switch queue to reach <paramref name="reached"/>, then checks it still holds a
        /// moment later. A switch still in flight would otherwise pass on a state it is about to leave.
        /// </summary>
        public async Task Settles(Func<bool> reached)
        {
            var deadline = DateTime.UtcNow + WaitLimit;
            while (!reached() && DateTime.UtcNow < deadline)
                await Task.Delay(5);

            Assert.True(reached(), "The switch queue never reached the expected state");
            await Task.Delay(100);
            Assert.True(reached(), "The switch queue left the expected state again");
        }
    }
}
