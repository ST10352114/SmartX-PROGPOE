using SmartXOne.Shared.Telemetry;

namespace SmartXOne.Api.Sensors;

/// <summary>
/// Per-category historical-reading store backing one <see cref="SmartX.Shared.Contracts.SensorCategory"/>'s
/// worth of sensors with a single <see cref="TelemetryBatchBuffer{T}"/> - the PoE
/// "advanced arrays" jagged array, doing real work in the ingestion path (not just
/// the Phase 3 demo). Each registered sensor of this category claims one fixed
/// device row (Stage 1: <c>batches[deviceIndex][readingIndex]</c>); reading a
/// sensor's history back goes through <see cref="TelemetryBatchBuffer{T}.ToOptimisedList"/>
/// (Stage 2: flatten to a timestamp-ordered <see cref="List{T}"/>), filtered down
/// to that one sensor.
/// </summary>
/// <typeparam name="T">The category's payload type (<c>float</c>, <c>int</c> or <c>bool</c>).</typeparam>
internal sealed class CategoryHistory<T>
    where T : struct
{
    private const int MaxDevices = 32;
    private const int MaxReadingsPerDevice = 20;

    private readonly TelemetryBatchBuffer<T> _buffer = new(MaxDevices);
    private readonly Dictionary<string, int> _deviceIndexBySensorId = new(StringComparer.OrdinalIgnoreCase);
    private int _nextDeviceIndex;

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

        _deviceIndexBySensorId[sensorId] = _nextDeviceIndex++;
        return true;
    }

    /// <summary>
    /// Appends a reading to the sensor's row, dropping the oldest entry once the
    /// row reaches <see cref="MaxReadingsPerDevice"/> - a bounded sliding window
    /// rather than unbounded growth, while still going through the array's
    /// replace-the-row API (<see cref="TelemetryBatchBuffer{T}.SetDeviceBatch"/>).
    /// </summary>
    public void Append(string sensorId, TelemetryPacket<T> reading)
    {
        var deviceIndex = _deviceIndexBySensorId[sensorId];
        var existing = _buffer.Batches[deviceIndex];

        var keepFromExisting = Math.Max(0, existing.Count + 1 - MaxReadingsPerDevice);
        var updated = new TelemetryPacket<T>[existing.Count - keepFromExisting + 1];

        var writeIndex = 0;
        for (var i = keepFromExisting; i < existing.Count; i++)
        {
            updated[writeIndex++] = existing[i];
        }

        updated[writeIndex] = reading;
        _buffer.SetDeviceBatch(deviceIndex, updated);
    }

    /// <summary>This sensor's own readings, oldest first.</summary>
    public List<TelemetryPacket<T>> GetSensorHistory(string sensorId)
        => _deviceIndexBySensorId.ContainsKey(sensorId)
            ? _buffer.ToOptimisedList()
                .Where(reading => string.Equals(reading.SensorId, sensorId, StringComparison.OrdinalIgnoreCase))
                .ToList()
            : [];
}
