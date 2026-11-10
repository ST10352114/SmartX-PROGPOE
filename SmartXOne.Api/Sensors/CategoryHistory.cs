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
    private readonly ILogger _logger;
    private int _nextDeviceIndex;

    public CategoryHistory(ILogger logger, Func<TelemetryPacket<T>, string?>? validate = null)
    {
        _logger = logger;
        _validate = validate;
    }

    /// <returns><c>false</c> if every device row in this category is already claimed.</returns>
    /// <exception cref="ArgumentException"><paramref name="sensorId"/> is null or blank.</exception>
    public bool TryReserveDevice(string sensorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sensorId);

        try
        {
            if (_deviceIndexBySensorId.ContainsKey(sensorId))
            {
                return true;
            }

            if (_nextDeviceIndex >= MaxDevices)
            {
                _logger.LogWarning("{Category} history is full ({MaxDevices} devices); {SensorId} was not reserved.",
                    typeof(T).Name, MaxDevices, sensorId);
                return false;
            }

            var deviceIndex = _nextDeviceIndex++;
            _deviceIndexBySensorId[sensorId] = deviceIndex;
            _ingestionBuffers[deviceIndex] = new IngestionBuffer<T>(MaxReadingsPerDevice, _validate);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reserve a {Category} history row for {SensorId}.", typeof(T).Name, sensorId);
            throw;
        }
    }

    /// <summary>
    /// Runs <paramref name="reading"/> through the device's
    /// <see cref="IngestionBuffer{T}"/> (Received -&gt; Validated -&gt;
    /// Stored/Rejected) and, if accepted, mirrors the buffer's current Stored
    /// readings into the Phase 3 jagged array so <see cref="GetSensorHistory"/>
    /// keeps working unchanged.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No history row was reserved for <paramref name="sensorId"/>.</exception>
    public IngestionEntry<T> Append(string sensorId, TelemetryPacket<T> reading)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sensorId);
        ArgumentNullException.ThrowIfNull(reading);

        try
        {
            if (!_deviceIndexBySensorId.TryGetValue(sensorId, out var deviceIndex))
            {
                throw new KeyNotFoundException($"No {typeof(T).Name} history row is reserved for sensor '{sensorId}'.");
            }

            var ingestionBuffer = _ingestionBuffers[deviceIndex]
                ?? throw new InvalidOperationException($"History row {deviceIndex} for '{sensorId}' was reserved but never initialised.");

            var entry = ingestionBuffer.Ingest(reading);

            if (entry.State == IngestionState.Stored)
            {
                _buffer.SetDeviceBatch(deviceIndex, ingestionBuffer.Select(e => e.Packet).ToArray());
            }

            return entry;
        }
        catch (KeyNotFoundException ex)
        {
            _logger.LogWarning(ex, "Reading for {SensorId} arrived with no history row reserved.", sensorId);
            throw;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "History buffer for {SensorId} is in an invalid state.", sensorId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to append a {Category} reading for {SensorId}.", typeof(T).Name, sensorId);
            throw;
        }
    }

    /// <summary>This sensor's own readings, oldest first - the Phase 3 jagged-array buffer's stage-2 flatten.</summary>
    public List<TelemetryPacket<T>> GetSensorHistory(string sensorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sensorId);

        try
        {
            return _deviceIndexBySensorId.ContainsKey(sensorId)
                ? _buffer.ToOptimisedList()
                    .Where(reading => string.Equals(reading.SensorId, sensorId, StringComparison.OrdinalIgnoreCase))
                    .ToList()
                : [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read the {Category} history for {SensorId}.", typeof(T).Name, sensorId);
            throw;
        }
    }

    /// <summary>
    /// This sensor's ingestion log: every currently-held Stored reading plus its
    /// recent Rejected attempts, oldest first - the custom collection's
    /// per-item state, made visible end to end (Fix B).
    /// </summary>
    public List<IngestionEntry<T>> GetIngestionLog(string sensorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sensorId);

        try
        {
            if (!_deviceIndexBySensorId.TryGetValue(sensorId, out var deviceIndex) || _ingestionBuffers[deviceIndex] is not { } buffer)
            {
                return [];
            }

            return buffer
                .Concat(buffer.Rejected)
                .OrderBy(e => e.Packet.Timestamp)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read the ingestion log for {SensorId}.", sensorId);
            throw;
        }
    }
}
