using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;

namespace OpenFreqClient.Services;

/// <summary>
/// The record of which radio slots are transmitting, and on which frequency. A slot transmits on at
/// most one frequency, and a frequency is transmitting while any slot holds it. Everything that
/// depends on our own transmissions (the frequencies playback mutes, the mic gate, own-TX status,
/// the frequencies the record callback sends on) is derived from this, so none of it can drift.
/// </summary>
internal sealed class ActiveTransmissions
{
    private readonly Lock _lock = new();
    private readonly Action<TransmissionChange> _onFrequenciesChanged;

    // Slot → the frequency it's transmitting on. Replaced wholesale under _lock. Readers take the
    // current snapshot without locking.
    private ImmutableDictionary<Guid, int> _bySlot = ImmutableDictionary<Guid, int>.Empty;

    /// <param name="onFrequenciesChanged">
    /// Receives each change to the set of transmitting frequencies. Called under the lock, so
    /// concurrent changes are reported in the order they happened.
    /// </param>
    public ActiveTransmissions(Action<TransmissionChange> onFrequenciesChanged)
    {
        _onFrequenciesChanged = onFrequenciesChanged;
    }

    public bool IsEmpty => _bySlot.IsEmpty;

    public bool IsTransmittingOn(int frequencyKhz) => _bySlot.Values.Contains(frequencyKhz);

    /// <summary>
    /// One slot per transmitting frequency, in frequency order. Which slot doesn't matter:
    /// <see cref="OpenFreqService.JoinFrequencyAsync"/> only lets cards from one location share a
    /// frequency, so they all transmit with the same position and power. Empty, without allocating,
    /// when nothing is transmitting, since the record callback calls this for every mic buffer.
    /// </summary>
    public IReadOnlyList<(int FrequencyKhz, Guid SlotId)> TransmittingSlots()
    {
        var table = _bySlot;
        if (table.IsEmpty) return [];

        return table
            .GroupBy(kvp => kvp.Value)
            .OrderBy(frequency => frequency.Key)
            .Select(frequency => (frequency.Key, frequency.First().Key))
            .ToList();
    }

    /// <summary>
    /// Keys <paramref name="slotId"/> on <paramref name="frequencyKhz"/>. Starting a slot that's
    /// already on that frequency changes nothing; starting it on another frequency moves it there.
    /// </summary>
    public TransmissionChange Start(Guid slotId, int frequencyKhz)
    {
        lock (_lock)
        {
            return Replace(_bySlot.SetItem(slotId, frequencyKhz));
        }
    }

    /// <summary>
    /// Ends <paramref name="slotId"/>'s transmission, if it has one. When
    /// <paramref name="onFrequencyKhz"/> is given, only a transmission on that frequency is ended.
    /// </summary>
    public TransmissionChange End(Guid slotId, int? onFrequencyKhz = null)
    {
        lock (_lock)
        {
            if (!_bySlot.TryGetValue(slotId, out var current) ||
                (onFrequencyKhz is { } frequencyKhz && current != frequencyKhz))
                return Replace(_bySlot);

            return Replace(_bySlot.Remove(slotId));
        }
    }

    public TransmissionChange EndAll()
    {
        lock (_lock)
        {
            return Replace(ImmutableDictionary<Guid, int>.Empty);
        }
    }

    // Swaps in the new table and reports how the transmitting frequencies changed. Caller holds _lock.
    private TransmissionChange Replace(ImmutableDictionary<Guid, int> next)
    {
        var previous = _bySlot;
        var before = previous.Values.ToHashSet();
        var after = next.Values.ToHashSet();

        _bySlot = next;
        var change = new TransmissionChange(previous.IsEmpty, next.IsEmpty, after,
            Started: after.Except(before).ToList(), Stopped: before.Except(after).ToList());

        if (!before.SetEquals(after))
            _onFrequenciesChanged(change);

        return change;
    }
}

/// <summary>What one change to <see cref="ActiveTransmissions"/> did.</summary>
/// <param name="WasIdle">Nothing was transmitting before the change.</param>
/// <param name="IsIdle">Nothing is transmitting after it.</param>
/// <param name="Frequencies">The frequencies transmitting after it.</param>
/// <param name="Started">Frequencies that began transmitting.</param>
/// <param name="Stopped">Frequencies that stopped transmitting.</param>
internal sealed record TransmissionChange(
    bool WasIdle, bool IsIdle, IReadOnlySet<int> Frequencies, IReadOnlyList<int> Started, IReadOnlyList<int> Stopped);
