using System.Collections;
using SmartXOne.Shared.Telemetry;

namespace SmartXOne.Shared.Collections;

/// <summary>
/// (PoE requirement 3B/Fix B &ndash; COLLECTIONS) A fixed-capacity, generic ring
/// buffer purpose-built for one sensor's ingestion history: readings arrive one
/// at a time via <see cref="Ingest"/>, each is validated and tagged with where
/// it ended up (<see cref="IngestionState"/>), and once the buffer is full the
/// oldest accepted reading is silently overwritten by the newest - no manual
/// array copying or shifting at the call site, which is what it replaces.
/// <para>
/// Implements <see cref="ICollection{T}"/> so it enumerates, counts, and
/// copies like any other collection; <see cref="Remove"/> is the one member
/// that doesn't make sense for a ring buffer (there's no "arbitrary middle
/// element" to remove without breaking its index invariants) and is the
/// documented exception - see its own remarks.
/// </para>
/// </summary>
/// <typeparam name="T">Reading value type (<c>float</c>, <c>int</c>, <c>bool</c>).</typeparam>
public sealed class IngestionBuffer<T> : ICollection<IngestionEntry<T>>
    where T : struct
{
    private readonly IngestionEntry<T>?[] _slots;
    private readonly Func<TelemetryPacket<T>, string?>? _validate;
    private readonly List<IngestionEntry<T>> _rejected = [];

    private int _head;  // physical index of the logically-oldest live slot
    private int _count;

    /// <param name="capacity">Maximum number of accepted readings held at once.</param>
    /// <param name="validate">
    /// Optional data-quality check run on every reading <see cref="Ingest"/>
    /// receives, in addition to whatever type/shape validation already happened
    /// before it got here. Returns <c>null</c> to accept, or a reason string to
    /// reject. Omit for a <typeparamref name="T"/> with no further rule to apply
    /// (e.g. a <c>bool</c> actuator state, which is always valid).
    /// </param>
    public IngestionBuffer(int capacity, Func<TelemetryPacket<T>, string?>? validate = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        Capacity = capacity;
        _slots = new IngestionEntry<T>[capacity];
        _validate = validate;
    }

    public int Capacity { get; }

    public int Count => _count;

    bool ICollection<IngestionEntry<T>>.IsReadOnly => false;

    /// <summary>Oldest-first indexer over the currently-held (<see cref="IngestionState.Stored"/>) readings.</summary>
    public IngestionEntry<T> this[int index]
    {
        get
        {
            if (index < 0 || index >= _count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return _slots[(_head + index) % Capacity]!;
        }
    }

    /// <summary>Readings rejected by <paramref name="validate"/>, oldest first, capped at <see cref="Capacity"/>.</summary>
    public IReadOnlyList<IngestionEntry<T>> Rejected => _rejected;

    /// <summary>
    /// Walks <paramref name="packet"/> through Received -&gt; Validated -&gt;
    /// Stored (accepted) or Received -&gt; Rejected (failed validation), storing
    /// it in whichever log applies, and returns the resulting entry.
    /// </summary>
    public IngestionEntry<T> Ingest(TelemetryPacket<T> packet)
    {
        var entry = new IngestionEntry<T> { Packet = packet, State = IngestionState.Received };

        var rejectionReason = _validate?.Invoke(packet);
        if (rejectionReason is not null)
        {
            entry.State = IngestionState.Rejected;
            entry.RejectionReason = rejectionReason;

            _rejected.Add(entry);
            if (_rejected.Count > Capacity)
            {
                _rejected.RemoveAt(0);
            }

            return entry;
        }

        entry.State = IngestionState.Validated;
        Add(entry); // sets State = Stored and performs the overwrite-oldest insert
        return entry;
    }

    /// <summary>Queries entries by their current <see cref="IngestionState"/> (<see cref="Rejected"/> included).</summary>
    public IEnumerable<IngestionEntry<T>> ByState(IngestionState state)
        => state == IngestionState.Rejected ? _rejected : this.Where(e => e.State == state);

    /// <summary>
    /// Direct ring insert: sets the entry to <see cref="IngestionState.Stored"/>
    /// and writes it to the next slot, overwriting the oldest entry once the
    /// buffer is at <see cref="Capacity"/>. Most callers should go through
    /// <see cref="Ingest"/> instead, which runs validation first.
    /// </summary>
    public void Add(IngestionEntry<T> entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry.State = IngestionState.Stored;

        var writeIndex = (_head + _count) % Capacity;
        _slots[writeIndex] = entry;

        if (_count < Capacity)
        {
            _count++;
        }
        else
        {
            _head = (_head + 1) % Capacity; // we just overwrote the oldest slot
        }
    }

    public void Clear()
    {
        Array.Clear(_slots);
        _head = 0;
        _count = 0;
        _rejected.Clear();
    }

    public bool Contains(IngestionEntry<T> item) => this.Any(e => ReferenceEquals(e, item));

    public void CopyTo(IngestionEntry<T>[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        for (var i = 0; i < _count; i++)
        {
            array[arrayIndex + i] = this[i];
        }
    }

    /// <remarks>
    /// Not supported: a ring buffer's whole point is that the oldest entry is
    /// overwritten automatically as new ones arrive, so there's no well-defined
    /// way to remove one specific entry from the middle without breaking the
    /// buffer's index invariants for everything after it. Use <see cref="Clear"/>
    /// to reset the buffer instead.
    /// </remarks>
    public bool Remove(IngestionEntry<T> item)
        => throw new NotSupportedException(
            $"{nameof(IngestionBuffer<T>)} overwrites its oldest entry automatically and does not support removing an arbitrary one. Use Clear() to reset it.");

    public IEnumerator<IngestionEntry<T>> GetEnumerator()
    {
        for (var i = 0; i < _count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
