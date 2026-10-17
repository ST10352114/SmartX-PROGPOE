using SmartXOne.Shared.Contracts;

namespace SmartXOne.Api.Realtime;

/// <summary>Part 2 endpoints: telemetry intake, the live device registry, and the command stream (issue / shut-all-valves / undo).</summary>
public static class RealtimeEndpoints
{
    public static void MapRealtimeEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/realtime");

        // 1. Telemetry intake - classifies severity, then enqueues (critical
        // bypasses to the priority queue) and returns immediately. Processing
        // happens asynchronously in TelemetryIngestionBackgroundService.
        group.MapPost("/telemetry", (SubmitTelemetryRequest request, TelemetryQueueService queue) =>
        {
            if (string.IsNullOrWhiteSpace(request.DeviceId))
            {
                return Results.BadRequest("DeviceId is required.");
            }

            var severity = TelemetrySeverity.Classify(request.DeviceKind, request.Value);
            var envelope = new TelemetryEnvelope(request.DeviceId, request.DeviceKind, request.Value, request.Unit, severity, DateTimeOffset.UtcNow);
            queue.Enqueue(envelope);

            return Results.Accepted(value: new { severity, queue.StandardQueueDepth, queue.PriorityQueueDepth });
        });

        group.MapGet("/queue-status", (TelemetryQueueService queue) => Results.Ok(queue.GetStatus()));

        // 2. Live device registry.
        group.MapGet("/devices", (DeviceRegistry registry) => Results.Ok(registry.GetAll()));

        group.MapGet("/devices/{id}", (string id, DeviceRegistry registry)
            => registry.Get(id) is { } device ? Results.Ok(device) : Results.NotFound($"No device with id '{id}'."));

        group.MapGet("/devices/{id}/timeline", (string id, DeviceRegistry registry)
            => registry.GetTimeline(id) is { } timeline
                ? Results.Ok(timeline)
                : Results.NotFound($"No device with id '{id}'."));

        // 3. Disconnect signal - HashSet dedup means a second signal for an
        // already-disconnected device is just ignored, not a new alert.
        group.MapPost("/devices/{id}/disconnect", (string id, DeviceRegistry registry) =>
        {
            if (registry.Get(id) is null)
            {
                return Results.NotFound($"No device with id '{id}'.");
            }

            var isNewDisconnect = registry.MarkDisconnected(id);
            return Results.Ok(new { deviceId = id, isNewDisconnect });
        });

        // 4. Single-device commands (pump / relay).
        group.MapPost("/devices/{id}/commands", (string id, IssueCommandRequest request, DeviceRegistry registry, CommandHistory history) =>
        {
            if (request.Type == DeviceCommandType.ShutAllValves)
            {
                return Results.BadRequest("ShutAllValves is system-wide - use POST /api/realtime/commands/shut-all-valves instead.");
            }

            var previousState = registry.ApplySingleDeviceCommand(id, request.Type);
            history.Push(new DeviceCommandDto(id, request.Type, DateTimeOffset.UtcNow, new Dictionary<string, DeviceStateDto> { [id] = previousState }));

            return Results.Ok(registry.Get(id));
        });

        // 5. System-wide command: shut every device's valve.
        group.MapPost("/commands/shut-all-valves", (DeviceRegistry registry, CommandHistory history) =>
        {
            var previousStates = registry.ShutAllValves();
            history.Push(new DeviceCommandDto(DeviceId: null, DeviceCommandType.ShutAllValves, DateTimeOffset.UtcNow, previousStates));
            return Results.Ok(new { affectedDevices = previousStates.Count });
        });

        // 6. Undo: pop the most recent command and restore every state it changed.
        group.MapPost("/commands/undo", (DeviceRegistry registry, CommandHistory history) =>
        {
            var command = history.Pop();
            if (command is null)
            {
                return Results.BadRequest("Nothing to undo.");
            }

            registry.RestoreStates(command.PreviousStates);
            return Results.Ok(new UndoResultDto(command.Type, command.PreviousStates));
        });

        group.MapGet("/commands", (CommandHistory history) => Results.Ok(history.Recent(20)));
    }
}
