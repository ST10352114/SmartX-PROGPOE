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
/// </summary>
public sealed class DeviceRegistry
{
    private readonly Dictionary<string, Device> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SortedDictionary<DateTime, TelemetryPointDto>> _timelines = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _disconnectedDeviceIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    private const int MaxTimelinePointsPerDevice = 100;

    /// <summary>Applies a dequeued reading: updates the device, appends to its timeline, and clears any disconnect flag (receiving telemetry means it's alive).</summary>
    internal void ApplyTelemetry(TelemetryEnvelope envelope)
    {
        lock (_gate)
        {
            var device = GetOrCreate(envelope.DeviceId, envelope.DeviceKind);
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
        }
    }

    /// <returns><c>true</c> if this is a new disconnect (not already flagged); <c>false</c> if it was a duplicate and got ignored.</returns>
    public bool MarkDisconnected(string deviceId)
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

    public IReadOnlyList<DeviceDto> GetAll()
    {
        lock (_gate)
        {
            return _devices.Values
                .Select(d => d.ToDto(_disconnectedDeviceIds.Contains(d.Id)))
                .OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public DeviceDto? Get(string deviceId)
    {
        lock (_gate)
        {
            return _devices.TryGetValue(deviceId, out var device)
                ? device.ToDto(_disconnectedDeviceIds.Contains(deviceId))
                : null;
        }
    }

    /// <returns><c>null</c> if the device doesn't exist; otherwise its timeline, oldest first.</returns>
    public IReadOnlyList<TelemetryPointDto>? GetTimeline(string deviceId)
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

    /// <summary>
    /// Applies a single-device command, atomically under the same lock as
    /// every other mutation here - the device is never handed out for a
    /// caller to mutate on its own, which is what would make this unsafe.
    /// Creates the device (as an ActuatorNode) if a command arrives before
    /// any telemetry has.
    /// </summary>
    /// <returns>The device's state immediately before the command was applied.</returns>
    internal DeviceStateDto ApplySingleDeviceCommand(string deviceId, DeviceCommandType type)
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

    /// <summary>Closes every registered device's valve, atomically. Returns the previous state of each device that was changed.</summary>
    internal IReadOnlyDictionary<string, DeviceStateDto> ShutAllValves()
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

    /// <summary>Restores every listed device back to its recorded state - what an undo does.</summary>
    internal void RestoreStates(IReadOnlyDictionary<string, DeviceStateDto> states)
    {
        lock (_gate)
        {
            foreach (var (id, state) in states)
            {
                if (_devices.TryGetValue(id, out var device))
                {
                    device.ApplyState(state);
                }
            }
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
