using System.Text.Json;
using SmartXOne.Shared.Contracts;
using SmartXOne.Shared.Deployment;
using SmartXOne.Shared.Telemetry;

namespace SmartXOne.Api.Sensors;

public enum RegisterSensorResult
{
    Registered,
    DuplicateSensorId,
    UnknownDeploymentNode,
    CategoryCapacityReached,
}

/// <summary>
/// In-memory store for registered sensors (requirement: "no EF/database" - this
/// is a plain <see cref="Dictionary{TKey,TValue}"/> guarded by a lock, which is
/// enough for a single-process gateway). Registered as a singleton in
/// <c>Program.cs</c> so all requests share the same state for the app's lifetime.
/// <para>
/// Historical readings are buffered per category via <see cref="CategoryHistory{T}"/>
/// (PoE requirement 3c &ndash; ADVANCED ARRAYS, applied for real: a
/// <see cref="TelemetryBatchBuffer{T}"/> per category, one device row per sensor).
/// </para>
/// </summary>
public sealed class SensorRepository
{
    private readonly Dictionary<string, SensorRecord> _sensors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private readonly DeploymentNode _deploymentRoot = DeploymentSeed.BuildRoot();

    private readonly CategoryHistory<float> _environmentalHistory = new();
    private readonly CategoryHistory<int> _powerHistory = new();
    private readonly CategoryHistory<bool> _actuatorHistory = new();

    public RegisterSensorResult Register(RegisterSensorRequest request)
    {
        if (!DeploymentTreeValidator.ContainsNode(_deploymentRoot, request.DeploymentNodeId))
        {
            return RegisterSensorResult.UnknownDeploymentNode;
        }

        lock (_gate)
        {
            if (_sensors.ContainsKey(request.SensorId))
            {
                return RegisterSensorResult.DuplicateSensorId;
            }

            var reserved = request.Category switch
            {
                SensorCategory.Environmental => _environmentalHistory.TryReserveDevice(request.SensorId),
                SensorCategory.PowerConsumption => _powerHistory.TryReserveDevice(request.SensorId),
                SensorCategory.Actuator => _actuatorHistory.TryReserveDevice(request.SensorId),
                _ => false,
            };

            if (!reserved)
            {
                return RegisterSensorResult.CategoryCapacityReached;
            }

            _sensors[request.SensorId] = new SensorRecord(
                request.SensorId, request.DeploymentNodeId, request.Category, request.DisplayName);
            return RegisterSensorResult.Registered;
        }
    }

    public IReadOnlyList<SensorSummary> GetAll()
    {
        lock (_gate)
        {
            return _sensors.Values.Select(s => s.ToSummary()).ToList();
        }
    }

    public SensorSummary? GetSummary(string sensorId)
    {
        lock (_gate)
        {
            return _sensors.TryGetValue(sensorId, out var record) ? record.ToSummary() : null;
        }
    }

    /// <summary>
    /// This sensor's stored reading history, oldest first - the
    /// <see cref="CategoryHistory{T}.GetSensorHistory"/> stage-2 <see cref="List{T}"/>
    /// conversion, exposed for the ingestion UI to display.
    /// </summary>
    /// <returns><c>null</c> if no sensor is registered with <paramref name="sensorId"/>.</returns>
    public IReadOnlyList<TelemetryReadingDto>? GetHistory(string sensorId)
    {
        lock (_gate)
        {
            if (!_sensors.TryGetValue(sensorId, out var record))
            {
                return null;
            }

            return record.Category switch
            {
                SensorCategory.Environmental => _environmentalHistory.GetSensorHistory(sensorId)
                    .Select(ToDto).ToList(),
                SensorCategory.PowerConsumption => _powerHistory.GetSensorHistory(sensorId)
                    .Select(ToDto).ToList(),
                SensorCategory.Actuator => _actuatorHistory.GetSensorHistory(sensorId)
                    .Select(ToDto).ToList(),
                _ => [],
            };
        }
    }

    /// <summary>
    /// Builds a <see cref="TelemetryPacket{T}"/> whose <c>T</c> matches the
    /// sensor's <see cref="SensorCategory"/> (float / int / bool), appends it to
    /// that category's history buffer, and computes the change since the
    /// previous reading via <c>SensorReading operator -</c>.
    /// <para>
    /// (PoE requirement 3a &ndash; GENERICS, applied) <c>T</c> can't be known at
    /// compile time here - it depends on data looked up at runtime - so each
    /// branch below calls <see cref="TelemetryPacket.Create{T}"/> with a
    /// concrete, compile-time-known <c>T</c>. The three branches unify through
    /// the non-generic <see cref="ITelemetryPacket"/> view, which is exactly
    /// what that interface is for.
    /// </para>
    /// </summary>
    /// <exception cref="KeyNotFoundException">No sensor registered with that id.</exception>
    /// <exception cref="JsonException"><paramref name="value"/> doesn't match the sensor's category.</exception>
    public TelemetryReadingDto SubmitReading(string sensorId, JsonElement value, string? unit)
    {
        SensorRecord record;
        lock (_gate)
        {
            if (!_sensors.TryGetValue(sensorId, out record!))
            {
                throw new KeyNotFoundException($"No sensor registered with id '{sensorId}'.");
            }
        }

        var resolvedUnit = unit ?? record.Category.DefaultUnit();

        lock (_gate)
        {
            switch (record.Category)
            {
                case SensorCategory.Environmental:
                {
                    var packet = TelemetryPacket.Create(sensorId, value.GetSingle(), resolvedUnit);
                    _environmentalHistory.Append(sensorId, packet);
                    record.ChangeSinceLast = ComputeChange(_environmentalHistory.GetSensorHistory(sensorId));
                    record.LatestReading = packet;
                    break;
                }

                case SensorCategory.PowerConsumption:
                {
                    var packet = TelemetryPacket.Create(sensorId, value.GetInt32(), resolvedUnit);
                    _powerHistory.Append(sensorId, packet);
                    record.ChangeSinceLast = ComputeChange(_powerHistory.GetSensorHistory(sensorId));
                    record.LatestReading = packet;
                    break;
                }

                case SensorCategory.Actuator:
                {
                    // A bool state has no numeric delta - SensorReading.Value is a
                    // double, so there's nothing meaningful to subtract here.
                    var packet = TelemetryPacket.Create(sensorId, value.GetBoolean(), resolvedUnit);
                    _actuatorHistory.Append(sensorId, packet);
                    record.ChangeSinceLast = null;
                    record.LatestReading = packet;
                    break;
                }

                default:
                    throw new ArgumentOutOfRangeException(nameof(sensorId), record.Category, message: null);
            }

            return new TelemetryReadingDto(
                record.LatestReading!.Unit,
                record.LatestReading!.Timestamp,
                record.LatestReading!.ValueType,
                record.LatestReading!.FormatValue(),
                record.ChangeSinceLast);
        }
    }

    /// <returns><c>false</c> if no sensor is registered with <paramref name="sensorId"/>.</returns>
    public bool SetProfileFile(string sensorId, string fileName)
    {
        lock (_gate)
        {
            if (!_sensors.TryGetValue(sensorId, out var record))
            {
                return false;
            }

            record.ProfileFileName = fileName;
            return true;
        }
    }

    private static TelemetryReadingDto ToDto<T>(TelemetryPacket<T> packet)
        where T : struct
        => new(packet.Unit, packet.Timestamp, packet.ValueType, packet.FormatValue(), ChangeSinceLast: null);

    /// <summary>
    /// (PoE requirement 3b &ndash; OPERATOR OVERLOADING, applied) The delta
    /// between the two most recent readings, via <c>SensorReading operator -</c>
    /// exactly as that struct's own doc comment describes it: "how much a value
    /// jumped between consecutive samples."
    /// </summary>
    private static double? ComputeChange(List<TelemetryPacket<float>> history)
    {
        if (history.Count < 2)
        {
            return null;
        }

        var previous = history[^2];
        var current = history[^1];
        var delta = new SensorReading(current.SensorId, current.Value, current.Unit, current.Timestamp)
                    - new SensorReading(previous.SensorId, previous.Value, previous.Unit, previous.Timestamp);
        return delta.Value;
    }

    private static double? ComputeChange(List<TelemetryPacket<int>> history)
    {
        if (history.Count < 2)
        {
            return null;
        }

        var previous = history[^2];
        var current = history[^1];
        var delta = new SensorReading(current.SensorId, current.Value, current.Unit, current.Timestamp)
                    - new SensorReading(previous.SensorId, previous.Value, previous.Unit, previous.Timestamp);
        return delta.Value;
    }
}
