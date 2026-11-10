using System.Text.Json;
using SmartXOne.Shared.Collections;
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

/// <summary>A reading was well-formed (right CLR type) but failed a data-quality rule - see <see cref="PacketValidation"/>.</summary>
public sealed class ReadingRejectedException(string reason) : Exception(reason);

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
    private readonly ILogger<SensorRepository> _logger;

    private readonly CategoryHistory<float> _environmentalHistory;
    private readonly CategoryHistory<int> _powerHistory;
    private readonly CategoryHistory<bool> _actuatorHistory;

    public SensorRepository(ILogger<SensorRepository> logger)
    {
        _logger = logger;
        _environmentalHistory = new(logger, PacketValidation.Environmental);
        _powerHistory = new(logger, PacketValidation.Power);
        _actuatorHistory = new(logger, PacketValidation.Actuator);
    }

    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="ArgumentException">The sensor id or deployment node id is blank.</exception>
    public RegisterSensorResult Register(RegisterSensorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SensorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DeploymentNodeId);

        try
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
        catch (InvalidOperationException ex)
        {
            // DeploymentTreeValidator's depth cap: the configured tree is malformed.
            _logger.LogError(ex, "Deployment tree check failed while registering {SensorId} at {NodeId}.",
                request.SensorId, request.DeploymentNodeId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register sensor {SensorId}.", request.SensorId);
            throw;
        }
    }

    public IReadOnlyList<SensorSummary> GetAll()
    {
        try
        {
            lock (_gate)
            {
                return _sensors.Values.Select(s => s.ToSummary()).ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list registered sensors.");
            throw;
        }
    }

    /// <exception cref="ArgumentException"><paramref name="sensorId"/> is null or blank.</exception>
    public SensorSummary? GetSummary(string sensorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sensorId);

        try
        {
            lock (_gate)
            {
                return _sensors.TryGetValue(sensorId, out var record) ? record.ToSummary() : null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read sensor {SensorId}.", sensorId);
            throw;
        }
    }

    /// <summary>
    /// This sensor's stored reading history, oldest first - the
    /// <see cref="CategoryHistory{T}.GetSensorHistory"/> stage-2 <see cref="List{T}"/>
    /// conversion, exposed for the ingestion UI to display.
    /// </summary>
    /// <returns><c>null</c> if no sensor is registered with <paramref name="sensorId"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="sensorId"/> is null or blank.</exception>
    public IReadOnlyList<TelemetryReadingDto>? GetHistory(string sensorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sensorId);

        try
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read the reading history for {SensorId}.", sensorId);
            throw;
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
    /// <exception cref="ReadingRejectedException">
    /// The value parsed fine but failed the category's data-quality rule (see
    /// <see cref="PacketValidation"/>) - the reading's <c>IngestionBuffer</c>
    /// entry is tagged <see cref="IngestionState.Rejected"/> rather than stored.
    /// </exception>
    public TelemetryReadingDto SubmitReading(string sensorId, JsonElement value, string? unit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sensorId);

        try
        {
            return SubmitReadingCore(sensorId, value, unit);
        }
        catch (KeyNotFoundException ex)
        {
            _logger.LogWarning(ex, "Reading submitted for unregistered sensor {SensorId}.", sensorId);
            throw;
        }
        catch (ReadingRejectedException ex)
        {
            _logger.LogInformation("Reading for {SensorId} rejected by validation: {Reason}", sensorId, ex.Message);
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            // JsonElement.GetSingle/GetInt32/GetBoolean: wrong JSON kind, or a
            // number that doesn't fit the category's CLR type.
            _logger.LogWarning(ex, "Reading for {SensorId} has a {ValueKind} value that doesn't fit its category.", sensorId, value.ValueKind);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected failure storing a reading for {SensorId}.", sensorId);
            throw;
        }
    }

    private TelemetryReadingDto SubmitReadingCore(string sensorId, JsonElement value, string? unit)
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
                    var entry = _environmentalHistory.Append(sensorId, packet);
                    if (entry.State == IngestionState.Rejected)
                    {
                        throw new ReadingRejectedException(entry.RejectionReason!);
                    }

                    record.ChangeSinceLast = ComputeChange(_environmentalHistory.GetSensorHistory(sensorId));
                    record.LatestReading = packet;
                    break;
                }

                case SensorCategory.PowerConsumption:
                {
                    var packet = TelemetryPacket.Create(sensorId, value.GetInt32(), resolvedUnit);
                    var entry = _powerHistory.Append(sensorId, packet);
                    if (entry.State == IngestionState.Rejected)
                    {
                        throw new ReadingRejectedException(entry.RejectionReason!);
                    }

                    record.ChangeSinceLast = ComputeChange(_powerHistory.GetSensorHistory(sensorId));
                    record.LatestReading = packet;
                    break;
                }

                case SensorCategory.Actuator:
                {
                    // A bool state has no numeric delta - SensorReading.Value is a
                    // double, so there's nothing meaningful to subtract here.
                    // (PacketValidation.Actuator never rejects, but Append is still
                    // routed through the same Received/Validated/Stored pipeline as
                    // the other two categories for consistency.)
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

    /// <summary>
    /// This sensor's ingestion log (Fix B): every currently-Stored reading plus
    /// its recent Rejected attempts, oldest first - see
    /// <see cref="CategoryHistory{T}.GetIngestionLog"/>.
    /// </summary>
    /// <returns><c>null</c> if no sensor is registered with <paramref name="sensorId"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="sensorId"/> is null or blank.</exception>
    public IReadOnlyList<IngestionLogEntryDto>? GetIngestionLog(string sensorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sensorId);

        try
        {
            lock (_gate)
            {
                if (!_sensors.TryGetValue(sensorId, out var record))
                {
                    return null;
                }

                return record.Category switch
                {
                    SensorCategory.Environmental => _environmentalHistory.GetIngestionLog(sensorId).Select(ToLogDto).ToList(),
                    SensorCategory.PowerConsumption => _powerHistory.GetIngestionLog(sensorId).Select(ToLogDto).ToList(),
                    SensorCategory.Actuator => _actuatorHistory.GetIngestionLog(sensorId).Select(ToLogDto).ToList(),
                    _ => [],
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read the ingestion log for {SensorId}.", sensorId);
            throw;
        }
    }

    /// <returns><c>false</c> if no sensor is registered with <paramref name="sensorId"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="sensorId"/> or <paramref name="storedFileName"/> is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="metadata"/> is null.</exception>
    public bool SetProfileFile(string sensorId, string storedFileName, AttachmentMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sensorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(storedFileName);
        ArgumentNullException.ThrowIfNull(metadata);

        try
        {
            lock (_gate)
            {
                if (!_sensors.TryGetValue(sensorId, out var record))
                {
                    return false;
                }

                record.AttachmentStoredFileName = storedFileName;
                record.Attachment = metadata;
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record attachment {StoredFileName} for {SensorId}.", storedFileName, sensorId);
            throw;
        }
    }

    /// <summary>
    /// The sensor's attachment metadata plus the name of its encrypted file on
    /// disk, for the download endpoint. <c>null</c> if the sensor isn't
    /// registered or has no attachment yet.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="sensorId"/> is null or blank.</exception>
    public (AttachmentMetadata Metadata, string StoredFileName)? GetAttachment(string sensorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sensorId);

        try
        {
            lock (_gate)
            {
                if (!_sensors.TryGetValue(sensorId, out var record) || record.Attachment is null || record.AttachmentStoredFileName is null)
                {
                    return null;
                }

                return (record.Attachment, record.AttachmentStoredFileName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read the attachment record for {SensorId}.", sensorId);
            throw;
        }
    }

    private static TelemetryReadingDto ToDto<T>(TelemetryPacket<T> packet)
        where T : struct
        => new(packet.Unit, packet.Timestamp, packet.ValueType, packet.FormatValue(), ChangeSinceLast: null);

    private static IngestionLogEntryDto ToLogDto<T>(IngestionEntry<T> entry)
        where T : struct
        => new(
            entry.Packet.FormatValue(),
            entry.Packet.ValueType,
            entry.Packet.Timestamp,
            entry.State.ToString(),
            entry.RejectionReason);

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
