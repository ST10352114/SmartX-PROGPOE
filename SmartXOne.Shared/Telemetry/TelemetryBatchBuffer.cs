namespace SmartXOne.Shared.Telemetry;

/// <summary>
/// (PoE requirement 3c &ndash; ADVANCED ARRAYS) Two-stage store for historical
/// telemetry.
/// <para>
/// <b>Stage 1 &ndash; jagged array.</b> Incoming data arrives as a fixed set of
/// devices, each with its own run of sequential readings. That shape maps
/// naturally onto a jagged array <c>TelemetryPacket&lt;T&gt;[][]</c> indexed as
/// <c>batches[deviceIndex][readingIndex]</c>. Each device's row can have a
/// different length, and the rows sit in contiguous memory, which is cheap to
/// fill batch-by-batch.
/// </para>
/// <para>
/// <b>Stage 2 &ndash; optimised list.</b> For ongoing use (appending new
/// readings, filtering, LINQ, binding to the UI) a flat <see cref="List{T}"/> is
/// better: it grows dynamically and is one sequence rather than a
/// row/column lookup. <see cref="ToOptimisedList"/> flattens the jagged array
/// once, pre-sizing the list to the exact total so no re-allocation happens
/// while copying, and orders the result by timestamp.
/// </para>
/// </summary>
/// <typeparam name="T">Reading value type (<c>float</c>, <c>int</c>, <c>bool</c>).</typeparam>
public sealed class TelemetryBatchBuffer<T>
    where T : struct
{
    private readonly TelemetryPacket<T>[][] _batches;

    /// <summary>Creates a buffer with one (initially empty) row per device.</summary>
    public TelemetryBatchBuffer(int deviceCount)
    {
        if (deviceCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deviceCount));
        }

        _batches = new TelemetryPacket<T>[deviceCount][];
        for (var i = 0; i < deviceCount; i++)
        {
            _batches[i] = [];
        }
    }

    /// <summary>Number of device rows.</summary>
    public int DeviceCount => _batches.Length;

    /// <summary>
    /// Stage 1 data, exposed so callers can see the raw
    /// <c>batches[deviceIndex][readingIndex]</c> structure before conversion.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<TelemetryPacket<T>>> Batches => _batches;

    /// <summary>Replaces one device's row of sequential readings.</summary>
    public void SetDeviceBatch(int deviceIndex, TelemetryPacket<T>[] readings)
    {
        ArgumentNullException.ThrowIfNull(readings);
        _batches[deviceIndex] = readings;
    }

    /// <summary>Total number of readings currently held across every device row.</summary>
    public int TotalReadings
    {
        get
        {
            var total = 0;
            foreach (var row in _batches)
            {
                total += row.Length;
            }

            return total;
        }
    }

    /// <summary>
    /// Stage 2: flatten the jagged array into a single timestamp-ordered
    /// <see cref="List{T}"/>, pre-sized to <see cref="TotalReadings"/>.
    /// </summary>
    public List<TelemetryPacket<T>> ToOptimisedList()
    {
        var flattened = new List<TelemetryPacket<T>>(TotalReadings);
        foreach (var deviceRow in _batches)
        {
            foreach (var reading in deviceRow)
            {
                flattened.Add(reading);
            }
        }

        flattened.Sort(static (a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return flattened;
    }
}
