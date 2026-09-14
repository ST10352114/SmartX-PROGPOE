using SmartXOne.Shared.Contracts;

namespace SmartXOne.Api.Sensors;

/// <summary>The Phase 4 endpoints: register, submit a reading, list, upload a profile file, and (Phase 6.1) fetch a sensor's stored history.</summary>
public static class SensorEndpoints
{
    public static void MapSensorEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/sensors");

        // 1. Register a sensor.
        group.MapPost("/", (RegisterSensorRequest request, SensorRepository repo) =>
        {
            if (string.IsNullOrWhiteSpace(request.SensorId) || string.IsNullOrWhiteSpace(request.DeploymentNodeId))
            {
                return Results.BadRequest("SensorId and DeploymentNodeId are required.");
            }

            return repo.Register(request) switch
            {
                RegisterSensorResult.Registered =>
                    Results.Created($"/api/sensors/{request.SensorId}", repo.GetSummary(request.SensorId)),
                RegisterSensorResult.DuplicateSensorId =>
                    Results.Conflict($"Sensor '{request.SensorId}' is already registered."),
                RegisterSensorResult.UnknownDeploymentNode =>
                    Results.BadRequest($"Deployment node '{request.DeploymentNodeId}' was not found in the configured deployment."),
                RegisterSensorResult.CategoryCapacityReached =>
                    Results.BadRequest($"No more '{request.Category}' sensors can be registered (history buffer is full)."),
                _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
            };
        });

        // 2. Submit a reading for a registered sensor.
        group.MapPost("/{sensorId}/readings", (string sensorId, SubmitReadingRequest request, SensorRepository repo) =>
        {
            try
            {
                return Results.Ok(repo.SubmitReading(sensorId, request.Value, request.Unit));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(ex.Message);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                return Results.BadRequest(
                    "Reading value doesn't match this sensor's category (expects a JSON number for Environmental/PowerConsumption, or a JSON boolean for Actuator).");
            }
        });

        // 3. Retrieve every registered sensor with its latest reading.
        group.MapGet("/", (SensorRepository repo) => Results.Ok(repo.GetAll()));

        // 3b. Retrieve one sensor's stored reading history (the advanced-arrays
        // buffer's stage-2 List<T> conversion, for the ingestion UI to display).
        group.MapGet("/{sensorId}/history", (string sensorId, SensorRepository repo)
            => repo.GetHistory(sensorId) is { } history
                ? Results.Ok(history)
                : Results.NotFound($"No sensor registered with id '{sensorId}'."));

        // 4. Attach a sensor profile file (multipart upload), stored on local disk.
        // DisableAntiforgery: antiforgery tokens guard browser form posts against
        // CSRF; this gateway has no such middleware configured (no cookie auth /
        // razor forms), so the automatic antiforgery requirement ASP.NET Core
        // attaches to form-bound endpoints doesn't apply here.
        group.MapPost("/{sensorId}/profile", async (string sensorId, IFormFile file, SensorRepository repo, IWebHostEnvironment env) =>
        {
            if (repo.GetSummary(sensorId) is null)
            {
                return Results.NotFound($"No sensor registered with id '{sensorId}'.");
            }

            if (file.Length == 0)
            {
                return Results.BadRequest("Uploaded file is empty.");
            }

            var dataDirectory = Path.Combine(env.ContentRootPath, "data");
            Directory.CreateDirectory(dataDirectory);

            // sensorId and the uploaded name both come from the caller, so neither is
            // trusted as a path: strip any directory portion from the original file
            // name and replace filesystem-invalid characters (sensorId is often a MAC
            // address, e.g. "AA:BB:CC:DD:EE:FF", and ':' isn't valid in a Windows file name).
            var safeSensorId = SanitiseForFileName(sensorId);
            var safeOriginalName = Path.GetFileName(file.FileName);
            var storedFileName = $"{safeSensorId}_{safeOriginalName}";

            await using var stream = File.Create(Path.Combine(dataDirectory, storedFileName));
            await file.CopyToAsync(stream);

            repo.SetProfileFile(sensorId, storedFileName);
            return Results.Ok(repo.GetSummary(sensorId));
        }).DisableAntiforgery();
    }

    private static string SanitiseForFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
