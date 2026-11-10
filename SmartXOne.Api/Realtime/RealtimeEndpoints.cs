using SmartXOne.Api.Infrastructure;
using SmartXOne.Api.Recommendations;
using SmartXOne.Shared.Contracts;

namespace SmartXOne.Api.Realtime;

/// <summary>Part 2 endpoints: telemetry intake, the live device registry, and the command stream (issue / shut-all-valves / undo).</summary>
public static class RealtimeEndpoints
{
    public static void MapRealtimeEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/realtime");
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(RealtimeEndpoints));

        // 1. Telemetry intake - classifies severity, then enqueues (critical
        // bypasses to the priority queue) and returns immediately. Processing
        // happens asynchronously in TelemetryIngestionBackgroundService.
        group.MapPost("/telemetry", (SubmitTelemetryRequest request, TelemetryQueueService queue) =>
        {
            if (string.IsNullOrWhiteSpace(request.DeviceId))
            {
                return ApiProblem.BadRequest("DeviceId is required.");
            }

            try
            {
                var severity = TelemetrySeverity.Classify(request.DeviceKind, request.Value);
                var envelope = new TelemetryEnvelope(request.DeviceId, request.DeviceKind, request.Value, request.Unit, severity, DateTimeOffset.UtcNow);
                queue.Enqueue(envelope);

                return Results.Accepted(value: new { severity, queue.StandardQueueDepth, queue.PriorityQueueDepth });
            }
            catch (ArgumentException ex)
            {
                return ApiProblem.Handled(logger, ex, StatusCodes.Status400BadRequest, "Submit telemetry", request.DeviceId);
            }
            catch (Exception ex)
            {
                return ApiProblem.Unexpected(logger, ex, "Submit telemetry", request.DeviceId);
            }
        });

        group.MapGet("/queue-status", (TelemetryQueueService queue) =>
        {
            try
            {
                return Results.Ok(queue.GetStatus());
            }
            catch (Exception ex)
            {
                return ApiProblem.Unexpected(logger, ex, "Read queue status", subject: null);
            }
        });

        // 2. Live device registry.
        group.MapGet("/devices", (DeviceRegistry registry) =>
        {
            try
            {
                return Results.Ok(registry.GetAll());
            }
            catch (Exception ex)
            {
                return ApiProblem.Unexpected(logger, ex, "List devices", subject: null);
            }
        });

        group.MapGet("/devices/{id}", (string id, DeviceRegistry registry) =>
        {
            try
            {
                return registry.Get(id) is { } device
                    ? Results.Ok(device)
                    : ApiProblem.NotFound($"No device with id '{id}'.");
            }
            catch (ArgumentException ex)
            {
                return ApiProblem.Handled(logger, ex, StatusCodes.Status400BadRequest, "Read device", id);
            }
            catch (Exception ex)
            {
                return ApiProblem.Unexpected(logger, ex, "Read device", id);
            }
        });

        group.MapGet("/devices/{id}/timeline", (string id, DeviceRegistry registry) =>
        {
            try
            {
                return registry.GetTimeline(id) is { } timeline
                    ? Results.Ok(timeline)
                    : ApiProblem.NotFound($"No device with id '{id}'.");
            }
            catch (ArgumentException ex)
            {
                return ApiProblem.Handled(logger, ex, StatusCodes.Status400BadRequest, "Read device timeline", id);
            }
            catch (Exception ex)
            {
                return ApiProblem.Unexpected(logger, ex, "Read device timeline", id);
            }
        });

        // 3. Disconnect signal - HashSet dedup means a second signal for an
        // already-disconnected device is just ignored, not a new alert (and
        // not logged as a fresh "condition" event for the recommendation
        // engine either).
        group.MapPost("/devices/{id}/disconnect", (string id, DeviceRegistry registry, RecommendationEngine recommendations) =>
        {
            try
            {
                if (registry.Get(id) is null)
                {
                    return ApiProblem.NotFound($"No device with id '{id}'.");
                }

                var isNewDisconnect = registry.MarkDisconnected(id);
                if (isNewDisconnect)
                {
                    recommendations.LogConditionDisconnected(id);
                }

                return Results.Ok(new { deviceId = id, isNewDisconnect });
            }
            catch (ArgumentException ex)
            {
                return ApiProblem.Handled(logger, ex, StatusCodes.Status400BadRequest, "Disconnect device", id);
            }
            catch (Exception ex)
            {
                return ApiProblem.Unexpected(logger, ex, "Disconnect device", id);
            }
        });

        // 4. Single-device commands (pump / relay).
        group.MapPost("/devices/{id}/commands", (string id, IssueCommandRequest request, DeviceRegistry registry, CommandHistory history, RecommendationEngine recommendations) =>
        {
            if (request.Type == DeviceCommandType.ShutAllValves)
            {
                return ApiProblem.BadRequest("ShutAllValves is system-wide - use POST /api/realtime/commands/shut-all-valves instead.");
            }

            try
            {
                var previousState = registry.ApplySingleDeviceCommand(id, request.Type);
                history.Push(new DeviceCommandDto(id, request.Type, DateTimeOffset.UtcNow, new Dictionary<string, DeviceStateDto> { [id] = previousState }));
                recommendations.LogCommand(id, request.Type);

                return Results.Ok(registry.Get(id));
            }
            catch (ArgumentException ex)
            {
                // Covers ArgumentOutOfRangeException for a command type that
                // isn't a single-device one.
                return ApiProblem.Handled(logger, ex, StatusCodes.Status400BadRequest, $"Issue {request.Type}", id);
            }
            catch (Exception ex)
            {
                return ApiProblem.Unexpected(logger, ex, $"Issue {request.Type}", id);
            }
        });

        // 5. System-wide command: shut every device's valve.
        group.MapPost("/commands/shut-all-valves", (DeviceRegistry registry, CommandHistory history, RecommendationEngine recommendations) =>
        {
            try
            {
                var previousStates = registry.ShutAllValves();
                history.Push(new DeviceCommandDto(DeviceId: null, DeviceCommandType.ShutAllValves, DateTimeOffset.UtcNow, previousStates));
                recommendations.LogCommand(deviceId: null, DeviceCommandType.ShutAllValves);
                return Results.Ok(new { affectedDevices = previousStates.Count });
            }
            catch (Exception ex)
            {
                return ApiProblem.Unexpected(logger, ex, "Shut all valves", subject: null);
            }
        });

        // 6. Undo: pop the most recent command and restore every state it changed.
        group.MapPost("/commands/undo", (DeviceRegistry registry, CommandHistory history) =>
        {
            DeviceCommandDto? command = null;
            try
            {
                command = history.Pop();
                if (command is null)
                {
                    return ApiProblem.BadRequest("Nothing to undo.");
                }

                registry.RestoreStates(command.PreviousStates);
                return Results.Ok(new UndoResultDto(command.Type, command.PreviousStates));
            }
            catch (Exception ex)
            {
                if (command is not null)
                {
                    // Put it back so the user can retry the undo instead of losing it.
                    history.Push(command);
                }

                return ApiProblem.Unexpected(logger, ex, "Undo last command", command?.DeviceId);
            }
        });

        group.MapGet("/commands", (CommandHistory history) =>
        {
            try
            {
                return Results.Ok(history.Recent(20));
            }
            catch (Exception ex)
            {
                return ApiProblem.Unexpected(logger, ex, "List recent commands", subject: null);
            }
        });
    }
}
