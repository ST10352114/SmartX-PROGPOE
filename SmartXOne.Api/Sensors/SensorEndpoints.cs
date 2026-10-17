using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using SmartXOne.Api.Uploads;
using SmartXOne.Shared.Contracts;

namespace SmartXOne.Api.Sensors;

/// <summary>The Phase 4 endpoints: register, submit a reading, list, upload a profile file, and (Phase 6.1) fetch a sensor's stored history.</summary>
public static class SensorEndpoints
{
    /// <summary>Hard ceiling on a single attachment, matching the Client's own <c>MaxProfileFileBytes</c>.</summary>
    private const long MaxAttachmentSizeBytes = 20 * 1024 * 1024;

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

        // 4. Attach a sensor profile file: streamed straight from the multipart
        // request body to disk, section by section, instead of buffering the
        // whole upload into an IFormFile first.
        //
        // Code Attribution
        // Purpose: Pattern for reading a multipart request with MultipartReader
        //          section-by-section instead of IFormFile model binding, so the
        //          file content is never buffered into memory as a whole.
        // Author: Microsoft
        // Title: Upload files in ASP.NET Core - Upload large files with streaming
        // Year: 2026
        // URL: https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads
        // Accessed: 17 October 2026
        //
        // DisableAntiforgery: antiforgery tokens guard browser form posts against
        // CSRF; this gateway has no such middleware configured (no cookie auth /
        // razor forms), so the automatic antiforgery requirement ASP.NET Core
        // attaches to form-bound endpoints doesn't apply here.
        group.MapPost("/{sensorId}/profile", async (
            string sensorId,
            HttpRequest request,
            SensorRepository repo,
            IWebHostEnvironment env,
            CancellationToken ct) =>
        {
            if (repo.GetSummary(sensorId) is null)
            {
                return Results.NotFound($"No sensor registered with id '{sensorId}'.");
            }

            if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
                || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest("Expected a multipart/form-data request.");
            }

            var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
            if (string.IsNullOrWhiteSpace(boundary))
            {
                return Results.BadRequest("Multipart boundary is missing.");
            }

            // BodyLengthLimit is MultipartReader's own outer safety net across every
            // section it reads; CopyWithLimitAsync below enforces the same limit on
            // just the file section so a breach gets a clean, specific 400 instead
            // of a generic stream fault.
            var reader = new MultipartReader(boundary, request.Body) { BodyLengthLimit = MaxAttachmentSizeBytes };

            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(ct)) is not null)
            {
                var contentDisposition = section.GetContentDispositionHeader();
                if (contentDisposition is null || !contentDisposition.DispositionType.Equals("form-data") || !contentDisposition.FileName.HasValue)
                {
                    continue; // a non-file form field, or a part we don't care about - move on without buffering it.
                }

                var originalFileName = Path.GetFileName(HeaderUtilities.RemoveQuotes(contentDisposition.FileName.Value).Value ?? string.Empty);
                var extension = Path.GetExtension(originalFileName) ?? string.Empty;

                if (!FileSignature.IsAllowedExtension(extension))
                {
                    return Results.BadRequest(
                        $"File type '{extension}' is not allowed. Allowed types: {FileSignature.AllowedExtensionsDisplay}.");
                }

                // Sniff just the first few bytes to confirm the content actually
                // matches the claimed type, before committing to write anything else.
                var header = new byte[FileSignature.SniffLength];
                var headerRead = await section.Body.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);

                if (!FileSignature.MatchesContent(extension, header.AsSpan(0, headerRead)))
                {
                    return Results.BadRequest($"File content does not match its extension ('{extension}').");
                }

                var dataDirectory = Path.Combine(env.ContentRootPath, "data");
                Directory.CreateDirectory(dataDirectory);

                // sensorId comes from the caller, so it isn't trusted as a path
                // fragment (sensorId is often a MAC address, e.g. "AA:BB:CC:DD:EE:FF",
                // and ':' isn't valid in a Windows file name). A GUID segment keeps
                // repeated uploads for the same sensor from overwriting each other.
                var safeSensorId = SanitiseForFileName(sensorId);
                var storedFileName = $"{safeSensorId}_{Guid.NewGuid():N}{extension}";
                var destinationPath = Path.Combine(dataDirectory, storedFileName);

                var fileStream = new FileStream(
                    destinationPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);

                try
                {
                    await using (fileStream)
                    {
                        await fileStream.WriteAsync(header.AsMemory(0, headerRead), ct);
                        await StreamCopy.CopyWithLimitAsync(section.Body, fileStream, MaxAttachmentSizeBytes - headerRead, ct);
                    }
                }
                catch (Exception ex) when (ex is AttachmentTooLargeException or InvalidDataException)
                {
                    // AttachmentTooLargeException comes from our own per-file check
                    // (StreamCopy.CopyWithLimitAsync); InvalidDataException is
                    // MultipartReader's own BodyLengthLimit tripping first - both mean
                    // the same thing to the caller, so both get the same clean 400.
                    if (File.Exists(destinationPath))
                    {
                        File.Delete(destinationPath);
                    }

                    return Results.BadRequest(
                        $"File exceeds the maximum allowed size of {MaxAttachmentSizeBytes / (1024 * 1024)} MB.");
                }

                repo.SetProfileFile(sensorId, storedFileName);
                return Results.Ok(repo.GetSummary(sensorId));
            }

            return Results.BadRequest("No file part found in the request.");
        }).DisableAntiforgery();
    }

    private static string SanitiseForFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
