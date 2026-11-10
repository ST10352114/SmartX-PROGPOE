using SmartXOne.Shared.Contracts;

namespace SmartXOne.Api.Realtime;

/// <summary>
/// (Part 2 requirements - DICTIONARY, SORTED DICTIONARY, HASH SET, all in one
/// store because they describe the same devices and need to change together
/// under one lock) The live registry the background consumer updates as it
/// drains <see cref="TelemetryQueueService"/>, and commands/undo read and
/// mutate directly.
/// <list type="bullet">
///   <item><see cref="Dictionary{TKey,TValue}"/> <c>_devices</c>: O(1) lookup
///     by device id as each queued packet is applied.</item>
///   <item><see cref="SortedDictionary{TKey,TValue}"/> per device: readings
///     keyed by timestamp, so the timeline endpoint returns them already
///     time-ordered for the frontend's graphs - no separate sort step.</item>
///   <item><see cref="HashSet{T}"/> <c>_disconnectedDeviceIds</c>: a device
///     id is either in the set or not, so a second disconnect signal for the
///     same device is a no-op (<see cref="HashSet{T}.Add"/> returns
///     <see langword="false"/>) rather than creating a duplicate alert.</item>
/// </list>
/// None of the three is thread-safe, so every access goes through one lock
/// (deliberately one lock, not three - a command that touches both the
/// device dictionary and its timeline needs them to change together).
/// <para>
/// Every public/internal method validates its arguments up front, then wraps
/// its work in a try/catch that logs the failure with the device id and
/// operation before rethrowing, so a fault is traceable in the gateway log
/// and the caller (endpoint or background consumer) still decides how to
/// answer it.
/// </para>
/// </summary>
public sealed class DeviceRegistry(ILogger<DeviceRegistry> logger)
{
    private readonly Dictionary<string, Device> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SortedDictionary<DateTime, TelemetryPointDto>> _timelines = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _disconnectedDeviceIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    private const int MaxTimelinePointsPerDevice = 100;

    /// <summary>Applies a dequeued reading: updates the device, appends to its timeline, and clears any disconnect flag (receiving telemetry means it's alive).</summary>
    /// <returns><c>true</c> if this reading just pushed the device into AlertSeverity.Critical from a non-Critical state - the signal the recommendation engine logs as a "condition" event.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="envelope"/> is null.</exception>
    /// <exception cref="ArgumentException">The envelope has no device id.</exception>
    internal bool ApplyTelemetry(TelemetryEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(envelope.DeviceId);

        try
        {
            lock (_gate)
            {
                var device = GetOrCreate(envelope.DeviceId, envelope.DeviceKind);
                var wasCritical = device.LastSeverity == AlertSeverity.Critical;
                device.LastValue = envelope.Value;
                device.Unit = envelope.Unit;
                device.LastSeen = envelope.Timestamp;
                device.LastSeverity = envelope.Severity;

                var timeline = GetOrCreateTimeline(envelope.DeviceId);
                timeline[envelope.Timestamp.UtcDateTime] = new TelemetryPointDto(envelope.Timestamp, envelope.Value, envelope.Unit, envelope.Severity);
                while (timeline.Count > MaxTimelinePointsPerDevice)
                {
                    timeline.Remove(timeline.Keys.First()); // SortedDictionary keeps Keys ordered, so this is always the oldest
                }

                _disconnectedDeviceIds.Remove(envelope.DeviceId);

                return !wasCritical && envelope.Severity == AlertSeverity.Critical;
            }
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Timeline for {DeviceId} was modified unexpectedly while trimming it.", envelope.DeviceId);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to apply telemetry {Value}{Unit} to {DeviceId}.", envelope.Value, envelope.Unit, envelope.DeviceId);
            throw;
        }
    }

    /// <returns><c>true</c> if this is a new disconnect (not already flagged); <c>false</c> if it was a duplicate and got ignored.</returns>
    /// <exception cref="ArgumentException"><paramref name="deviceId"/> is null or blank.</exception>
    public bool MarkDisconnected(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        try
        {
            lock (_gate)
            {
                if (!_devices.ContainsKey(deviceId))
                {
                    return false;
                }

                return _disconnectedDeviceIds.Add(deviceId);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to mark {DeviceId} as disconnected.", deviceId);
            throw;
        }
    }

    public IReadOnlyList<DeviceDto> GetAll()
    {
        try
        {
            lock (_gate)
            {
                return _devices.Values
                    .Select(d => d.ToDto(_disconnectedDeviceIds.Contains(d.Id)))
                    .OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to list registered devices.");
            throw;
        }
    }

    /// <exception cref="ArgumentException"><paramref name="deviceId"/> is null or blank.</exception>
    public DeviceDto? Get(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        try
        {
            lock (_gate)
            {
                return _devices.TryGetValue(deviceId, out var device)
                    ? device.ToDto(_disconnectedDeviceIds.Contains(deviceId))
                    : null;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read device {DeviceId}.", deviceId);
            throw;
        }
    }

    /// <returns><c>null</c> if the device doesn't exist; otherwise its timeline, oldest first.</returns>
    /// <exception cref="ArgumentException"><paramref name="deviceId"/> is null or blank.</exception>
    public IReadOnlyList<TelemetryPointDto>? GetTimeline(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        try
        {
            lock (_gate)
            {
                if (!_devices.ContainsKey(deviceId))
                {
                    return null;
                }

                return _timelines.TryGetValue(deviceId, out var timeline) ? timeline.Values.ToList() : [];
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read the timeline for {DeviceId}.", deviceId);
            throw;
        }
    }

    /// <summary>
    /// Applies a single-device command, atomically under the same lock as
    /// every other mutation here - the device is never handed out for a
    /// caller to mutate on its own, which is what would make this unsafe.
    /// Creates the device (as an ActuatorNode) if a command arrives before
    /// any telemetry has.
    /// </summary>
    /// <returns>The device's state immediately before the command was applied.</returns>
    /// <exception cref="ArgumentException"><paramref name="deviceId"/> is null or blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="type"/> isn't a single-device command.</exception>
    internal DeviceStateDto ApplySingleDeviceCommand(string deviceId, DeviceCommandType type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        try
        {
            lock (_gate)
            {
                var device = GetOrCreate(deviceId, RealtimeDeviceKind.ActuatorNode);
                var previous = device.State;

                switch (type)
                {
                    case DeviceCommandType.PumpOn: device.PumpOn = true; break;
                    case DeviceCommandType.PumpOff: device.PumpOn = false; break;
                    case DeviceCommandType.RelayOn: device.RelayOn = true; break;
                    case DeviceCommandType.RelayOff: device.RelayOn = false; break;
                    default: throw new ArgumentOutOfRangeException(nameof(type), type, "Not a single-device command.");
                }

                return previous;
            }
        }
        catch (ArgumentOutOfRangeException ex)
        {
            logger.LogWarning(ex, "Rejected command {CommandType} for {DeviceId}: not a single-device command.", type, deviceId);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to apply {CommandType} to {DeviceId}.", type, deviceId);
            throw;
        }
    }

    /// <summary>Closes every registered device's valve, atomically. Returns the previous state of each device that was changed.</summary>
    internal IReadOnlyDictionary<string, DeviceStateDto> ShutAllValves()
    {
        try
        {
            lock (_gate)
            {
                var previousStates = new Dictionary<string, DeviceStateDto>();
                foreach (var device in _devices.Values)
                {
                    if (device.ValveOpen)
                    {
                        previousStates[device.Id] = device.State;
                        device.ValveOpen = false;
                    }
                }

                return previousStates;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to shut all valves across {DeviceCount} devices.", _devices.Count);
            throw;
        }
    }

    /// <summary>Restores every listed device back to its recorded state - what an undo does.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="states"/> is null.</exception>
    internal void RestoreStates(IReadOnlyDictionary<string, DeviceStateDto> states)
    {
        ArgumentNullException.ThrowIfNull(states);

        try
        {
            lock (_gate)
            {
                foreach (var (id, state) in states)
                {
                    if (_devices.TryGetValue(id, out var device))
                    {
                        device.ApplyState(state);
                    }
                    else
                    {
                        logger.LogWarning("Undo skipped {DeviceId}: it is no longer registered.", id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to restore previous state for {DeviceIds}.", string.Join(", ", states.Keys));
            throw;
        }
    }

    private Device GetOrCreate(string deviceId, RealtimeDeviceKind kind)
    {
        if (_devices.TryGetValue(deviceId, out var existing))
        {
            return existing;
        }

        var device = new Device { Id = deviceId, Kind = kind };
        _devices[deviceId] = device;
        return device;
    }

    private SortedDictionary<DateTime, TelemetryPointDto> GetOrCreateTimeline(string deviceId)
    {
        if (_timelines.TryGetValue(deviceId, out var existing))
        {
            return existing;
        }

        var timeline = new SortedDictionary<DateTime, TelemetryPointDto>();
        _timelines[deviceId] = timeline;
        return timeline;
    }
}
