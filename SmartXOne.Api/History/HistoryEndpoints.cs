using SmartXOne.Api.Infrastructure;

namespace SmartXOne.Api.History;

/// <summary>Telemetry history: range queries over each device's AVL-indexed readings.</summary>
public static class HistoryEndpoints
{
    public static void MapHistoryEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/history");
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(HistoryEndpoints));

        group.MapGet("/", (TelemetryHistoryStore history) =>
        {
            try
            {
                return Results.Ok(history.GetDevices());
            }
            catch (Exception ex)
            {
                return ApiProblem.Unexpected(logger, ex, "List history devices", subject: null);
            }
        });

        // e.g. /api/history/AA:BB:CC:00:01:01?from=2026-11-10T10:00:00Z&to=2026-11-10T10:15:00Z&metric=temperature
        // An unparseable from/to is rejected as 400 by parameter binding before this runs.
        group.MapGet("/{deviceId}", (
            string deviceId,
            DateTimeOffset? from,
            DateTimeOffset? to,
            string? metric,
            int? limit,
            TelemetryHistoryStore history) =>
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return ApiProblem.BadRequest("A device id (MAC) is required.");
            }

            if (from is { } f && to is { } t && f > t)
            {
                return ApiProblem.BadRequest("'from' must not be after 'to'.");
            }

            if (limit is < 1 or > TelemetryHistoryStore.MaxLimit)
            {
                return ApiProblem.BadRequest($"'limit' must be between 1 and {TelemetryHistoryStore.MaxLimit}.");
            }

            try
            {
                var result = history.Query(deviceId, from, to, metric, limit ?? TelemetryHistoryStore.DefaultLimit);
                return result is null
                    ? ApiProblem.NotFound($"No telemetry history for device '{deviceId}'.")
                    : Results.Ok(result);
            }
            catch (ArgumentException ex)
            {
                return ApiProblem.Handled(logger, ex, StatusCodes.Status400BadRequest, "Query history", deviceId);
            }
            catch (Exception ex)
            {
                return ApiProblem.Unexpected(logger, ex, "Query history", deviceId);
            }
        });
    }
}
