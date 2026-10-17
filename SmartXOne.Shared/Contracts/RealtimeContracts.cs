namespace SmartXOne.Shared.Contracts;

/// <summary>What kind of Part-2 device a telemetry envelope or command targets.</summary>
public enum RealtimeDeviceKind
{
    EnvironmentalSensor,
    SmartPlug,
    ActuatorNode,
}

/// <summary>
/// How urgent a telemetry reading is. Lower numeric value = more urgent, so it
/// doubles directly as a <c>PriorityQueue&lt;TElement,TPriority&gt;</c> priority
/// (the queue dequeues the smallest value first).
/// </summary>
public enum AlertSeverity
{
    Critical = 0,
    Elevated = 1,
    Normal = 2,
}

/// <param name="DeviceId">MAC address / unique node id.</param>
/// <param name="DeviceKind">Determines which severity rule classifies this reading.</param>
/// <param name="Value">The raw reading (moisture %, wattage W - interpretation depends on <paramref name="DeviceKind"/>).</param>
/// <param name="Unit">Unit of measure, for display.</param>
public record SubmitTelemetryRequest(string DeviceId, RealtimeDeviceKind DeviceKind, double Value, string Unit);

/// <param name="Type">Which single-device override to apply (not <see cref="DeviceCommandType.ShutAllValves"/> - that's its own endpoint, since it isn't scoped to one device).</param>
public record IssueCommandRequest(DeviceCommandType Type);

/// <summary>The manual overrides the command stream supports. The first four target one device; <see cref="ShutAllValves"/> is system-wide.</summary>
public enum DeviceCommandType
{
    PumpOn,
    PumpOff,
    RelayOn,
    RelayOff,
    ShutAllValves,
}

/// <summary>A device's controllable state - what a <see cref="DeviceCommandType"/> changes and an undo restores.</summary>
public record DeviceStateDto(bool PumpOn, bool RelayOn, bool ValveOpen);

/// <summary>Live snapshot of one device in the registry.</summary>
public record DeviceDto(
    string Id,
    RealtimeDeviceKind Kind,
    double LastValue,
    string Unit,
    DateTimeOffset LastSeen,
    AlertSeverity LastSeverity,
    bool IsDisconnected,
    DeviceStateDto State);

/// <summary>One point on a device's timeline (the <c>SortedDictionary&lt;DateTime,_&gt;</c> contents, already time-ordered).</summary>
public record TelemetryPointDto(DateTimeOffset Timestamp, double Value, string Unit, AlertSeverity Severity);

/// <summary>One entry from the command <c>Stack&lt;T&gt;</c>, newest first.</summary>
/// <param name="DeviceId"><c>null</c> for <see cref="DeviceCommandType.ShutAllValves"/> - it isn't scoped to one device.</param>
/// <param name="PreviousStates">
/// Every device state this command changed, by device id - exactly one entry
/// for a single-device command, one per affected device for ShutAllValves.
/// What an undo restores.
/// </param>
public record DeviceCommandDto(
    string? DeviceId,
    DeviceCommandType Type,
    DateTimeOffset IssuedAt,
    IReadOnlyDictionary<string, DeviceStateDto> PreviousStates);

/// <summary>Result of a successful undo: what was undone and every device's state after restoring it.</summary>
public record UndoResultDto(DeviceCommandType UndoneType, IReadOnlyDictionary<string, DeviceStateDto> RestoredStates);

/// <summary>Current depth of each queue - mostly for demonstrating/observing the ingestion pipeline.</summary>
public record QueueStatusDto(int StandardQueueDepth, int PriorityQueueDepth, long ProcessedCount);
