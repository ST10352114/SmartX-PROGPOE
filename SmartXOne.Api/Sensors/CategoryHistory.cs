using SmartXOne.Shared.Collections;
using SmartXOne.Shared.Telemetry;

namespace SmartXOne.Api.Sensors;

/// <summary>
/// Per-category historical-reading store backing one <see cref="SmartXOne.Shared.Contracts.SensorCategory"/>'s
/// worth of sensors.
/// <para>
/// Each device gets its own <see cref="IngestionBuffer{T}"/> (Fix B &ndash;
/// COLLECTIONS): a bounded ring buffer that validates every reading, tags it
/// Stored or Rejected, and overwrites its own oldest Stored entry once full -
/// replacing what used to be manual array-shift-and-truncate logic here.
/// </para>
/// <para>
/// The Phase 3 <see cref="TelemetryBatchBuffer{T}"/> (jagged array -&gt; stage-2
/// <see cref="List{T}"/>) is kept alongside it, deliberately: the ingestion
/// buffer is the new source of truth for what's held and its per-item state,
/// while the jagged array is still what <see cref="GetSensorHistory"/> reads
/// from, unchanged, so the Phase 3 "advanced arrays" requirement keeps running
/// in production exactly as it did before.
/// </para>
/// </summary>
/// <typeparam name="T">The category's payload type (<c>float</c>, <c>int</c> or <c>bool</c>).</typeparam>
internal sealed class CategoryHistory<T>
    where T : struct
{
    private const int MaxDevices = 32;
    private const int MaxReadingsPerDevice = 20;

    private readonly TelemetryBatchBuffer<T> _buffer = new(MaxDevices);
    private readonly IngestionBuffer<T>?[] _ingestionBuffers = new IngestionBuffer<T>?[MaxDevices];
    private readonly Dictionary<string, int> _deviceIndexBySensorId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<TelemetryPacket<T>, string?>? _validate;
    private int _nextDeviceIndex;

    public CategoryHistory(Func<TelemetryPacket<T>, string?>? validate = null)
    {
        _validate = validate;
    }

    /// <returns><c>false</c> if every device row in this category is already claimed.</returns>
    public bool TryReserveDevice(string sensorId)
    {
        if (_deviceIndexBySensorId.ContainsKey(sensorId))
        {
            return true;
        }

        if (_nextDeviceIndex >= MaxDevices)
        {
            return false;
        }

        var deviceIndex = _nextDeviceIndex++;
        _deviceIndexBySensorId[sensorId] = deviceIndex;
        _ingestionBuffers[deviceIndex] = new IngestionBuffer<T>(MaxReadingsPerDevice, _validate);
        return true;
    }

    /// <summary>
    /// Runs <paramref name="reading"/> through the device's
    /// <see cref="IngestionBuffer{T}"/> (Received -&gt; Validated -&gt;
    /// Stored/Rejected) and, if accepted, mirrors the buffer's current Stored
    /// readings into the Phase 3 jagged array so <see cref="GetSensorHistory"/>
    /// keeps working unchanged.
    /// </summary>
    public IngestionEntry<T> Append(string sensorId, TelemetryPacket<T> reading)
    {
        var deviceIndex = _deviceIndexBySensorId[sensorId];
        var ingestionBuffer = _ingestionBuffers[deviceIndex]!;

        var entry = ingestionBuffer.Ingest(reading);

        if (entry.State == IngestionState.Stored)
        {
            _buffer.SetDeviceBatch(deviceIndex, ingestionBuffer.Select(e => e.Packet).ToArray());
        }

        return entry;
    }

    /// <summary>This sensor's own readings, oldest first - the Phase 3 jagged-array buffer's stage-2 flatten.</summary>
    public List<TelemetryPacket<T>> GetSensorHistory(string sensorId)
        => _deviceIndexBySensorId.ContainsKey(sensorId)
            ? _buffer.ToOptimisedList()
                .Where(reading => string.Equals(reading.SensorId, sensorId, StringComparison.OrdinalIgnoreCase))
                .ToList()
            : [];

    /// <summary>
    /// This sensor's ingestion log: every currently-held Stored reading plus its
    /// recent Rejected attempts, oldest first - the custom collection's
    /// per-item state, made visible end to end (Fix B).
    /// </summary>
    public List<IngestionEntry<T>> GetIngestionLog(string sensorId)
    {
        if (!_deviceIndexBySensorId.TryGetValue(sensorId, out var deviceIndex))
        {
            return [];
        }

        var buffer = _ingestionBuffers[deviceIndex]!;
        return buffer
            .Concat(buffer.Rejected)
            .OrderBy(e => e.Packet.Timestamp)
            .ToList();
    }
}
