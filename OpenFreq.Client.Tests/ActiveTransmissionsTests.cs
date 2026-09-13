using OpenFreqClient.Services;

namespace OpenFreq.Client.Tests;

/// <summary>
/// Tests for <see cref="ActiveTransmissions"/>, the one record of our own transmissions that the
/// playback mutes are derived from.
/// </summary>
public class ActiveTransmissionsTests
{
    private const int Uhf = 251_000;
    private const int Vhf = 124_000;

    private static readonly Guid SlotA = Guid.NewGuid();
    private static readonly Guid SlotB = Guid.NewGuid();

    // Every set of frequencies pushed to playback, in order.
    private readonly List<int[]> _pushed = [];

    // Every change reported to the callback, in order.
    private readonly List<TransmissionChange> _reported = [];

    private readonly ActiveTransmissions _tx;

    public ActiveTransmissionsTests()
    {
        _tx = new ActiveTransmissions(change =>
        {
            _pushed.Add(change.Frequencies.Order().ToArray());
            _reported.Add(change);
        });
    }

    // What playback is muting now.
    private int[] Muted => _pushed.Count > 0 ? _pushed[^1] : [];

    [Fact]
    public void DoubleStart_SameSlot_ChangesNothing()
    {
        var first = _tx.Start(SlotA, Uhf);
        var second = _tx.Start(SlotA, Uhf);

        Assert.True(first.WasIdle);
        Assert.Equal([Uhf], first.Started);
        Assert.False(second.WasIdle);
        Assert.Empty(second.Started);
        Assert.Empty(second.Stopped);
        Assert.Single(_pushed);

        // One stop is enough to undo both starts.
        var end = _tx.End(SlotA);

        Assert.Equal([Uhf], end.Stopped);
        Assert.True(end.IsIdle);
        Assert.True(_tx.IsEmpty);
        Assert.Empty(Muted);
    }

    [Fact]
    public void TwoSlotsOnOneFrequency_StayKeyedUntilBothRelease()
    {
        _tx.Start(SlotA, Uhf);
        var secondStart = _tx.Start(SlotB, Uhf);

        Assert.Empty(secondStart.Started);

        var firstEnd = _tx.End(SlotA);

        Assert.Empty(firstEnd.Stopped);
        Assert.False(firstEnd.IsIdle);
        Assert.True(_tx.IsTransmittingOn(Uhf));
        Assert.Equal([Uhf], Muted);
        Assert.Equal((Uhf, SlotB), Assert.Single(_tx.TransmittingSlots()));

        var secondEnd = _tx.End(SlotB);

        Assert.Equal([Uhf], secondEnd.Stopped);
        Assert.True(secondEnd.IsIdle);
        Assert.Empty(Muted);
    }

    [Fact]
    public void TransmittingSlots_OneSlotPerFrequency_InFrequencyOrder()
    {
        var vhfSlot = Guid.NewGuid();
        _tx.Start(SlotA, Uhf);
        _tx.Start(SlotB, Uhf);
        _tx.Start(vhfSlot, Vhf);

        var slots = _tx.TransmittingSlots();

        Assert.Equal(2, slots.Count);
        Assert.Equal((Vhf, vhfSlot), slots[0]);
        Assert.Equal(Uhf, slots[1].FrequencyKhz);
        Assert.Contains(slots[1].SlotId, new[] { SlotA, SlotB });
    }

    [Fact]
    public void StartOnAnotherFrequency_MovesTheSlot()
    {
        _tx.Start(SlotA, Uhf);

        var moved = _tx.Start(SlotA, Vhf);

        Assert.Equal([Vhf], moved.Started);
        Assert.Equal([Uhf], moved.Stopped);
        Assert.False(_tx.IsTransmittingOn(Uhf));
        Assert.True(_tx.IsTransmittingOn(Vhf));
        Assert.Equal([Vhf], Muted);
    }

    [Fact]
    public void EndOnFrequency_OnlyEndsATransmissionOnThatFrequency()
    {
        _tx.Start(SlotA, Vhf);

        // A late leave of a frequency the slot has since moved away from.
        var otherFrequency = _tx.End(SlotA, onFrequencyKhz: Uhf);

        Assert.Empty(otherFrequency.Stopped);
        Assert.True(_tx.IsTransmittingOn(Vhf));
        Assert.Equal([Vhf], Muted);

        var sameFrequency = _tx.End(SlotA, onFrequencyKhz: Vhf);

        Assert.Equal([Vhf], sameFrequency.Stopped);
        Assert.Empty(Muted);
    }

    [Fact]
    public void EndAll_StopsEveryFrequencyOnce()
    {
        _tx.Start(SlotA, Uhf);
        _tx.Start(SlotB, Uhf);
        _tx.Start(Guid.NewGuid(), Vhf);

        var change = _tx.EndAll();

        Assert.False(change.WasIdle);
        Assert.True(change.IsIdle);
        Assert.Equal([Vhf, Uhf], change.Stopped.Order());
        Assert.True(_tx.IsEmpty);
        Assert.Empty(_tx.TransmittingSlots());
        Assert.Empty(Muted);
    }

    [Fact]
    public void EndingWhileIdle_ChangesNothing()
    {
        var end = _tx.End(SlotA);
        var endAll = _tx.EndAll();

        Assert.True(end is { WasIdle: true, IsIdle: true });
        Assert.Empty(end.Stopped);
        Assert.True(endAll is { WasIdle: true, IsIdle: true });
        Assert.Empty(endAll.Stopped);
        Assert.Empty(_pushed);
    }

    [Fact]
    public void ConcurrentChanges_LastPushMatchesTheTable()
    {
        Guid[] slots = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        int[] frequencies = [Uhf, Vhf];

        Parallel.For(0, 10_000, i =>
        {
            var slot = slots[i % slots.Length];
            if (i % 2 == 0) _tx.Start(slot, frequencies[i / 2 % frequencies.Length]);
            else _tx.End(slot);
        });

        Assert.Equal(_tx.TransmittingSlots().Select(s => s.FrequencyKhz).Order(), Muted);
    }

    [Fact]
    public void Callback_ReportsOnlyChangesToTheFrequencies()
    {
        _tx.Start(SlotA, Uhf);
        _tx.Start(SlotB, Uhf); // Uhf is already transmitting.
        _tx.EndAll();

        Assert.Equal(2, _reported.Count);
        Assert.Equal([Uhf], _reported[0].Started);
        Assert.Empty(_reported[0].Stopped);
        Assert.Empty(_reported[1].Started);
        Assert.Equal([Uhf], _reported[1].Stopped);
        Assert.Empty(_reported[1].Frequencies);
    }
}
