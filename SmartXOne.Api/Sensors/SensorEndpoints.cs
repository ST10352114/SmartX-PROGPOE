using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using SmartXOne.Api.Infrastructure;
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
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SensorEndpoints));

        // 1. Register a sensor.
        group.MapPost("/", (RegisterSensorRequest request, SensorRepository repo) =>
        {
            if (string.IsNullOrWhiteSpace(request.SensorId) || string.IsNullOrWhiteSpace(request.DeploymentNodeId))
            {
                return ApiProblem.BadRequest("SensorId and DeploymentNodeId are required.");
            }

            try
            {
                return repo.Register(request) switch
                {
                    RegisterSensorResult.Registered =>
                        Results.Created($"/api/sensors/{request.SensorId}", repo.GetSummary(request.SensorId)),
                    RegisterSensorResult.DuplicateSensorId =>
                        ApiProblem.Conflict($"Sensor '{request.SensorId}' is already registered."),
                    RegisterSensorResult.UnknownDeploymentNode =>
                        ApiProblem.BadRequest($"Deployment node '{request.DeploymentNodeId}' was not found in the configured deployment."),
                    RegisterSensorResult.CategoryCapacityReached =>
                        ApiProblem.BadRequest($"No more '{request.Category}' sensors can be registered (history buffer is full)."),
                    _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
                };
            }
            catch (ArgumentException ex)
            {
                return ApiProblem.Handled(logger, ex, StatusCodes.Status400BadRequest, "Register sensor", request.SensorId);
            }
            catch (Exception ex)
            {
                return ApiProblem.Unexpected(logger, ex, "Register sensor", request.SensorId);
            }
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
                return ApiProblem.Handled(logger, ex, StatusCodes.Status404NotFound, "Submit reading", sensorId);
            }
            catch (ReadingRejectedException ex)
            {
                // Parsed fine, but PacketValidation rejected it - the reading is
                // still logged (GET .../ingestion-log) with this reason, just not
                // stored as the sensor's latest reading.
                return ApiProblem.Handled(logger, ex, StatusCodes.Status400BadRequest, "Submit reading", sensorId);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                logger.LogWarning(ex, "Reading for {SensorId} has the wrong JSON type for its category.", sensorId);
                return ApiProblem.BadRequest(
                    "Reading value doesn't match this sensor's category (expects a JSON number for Environmental/PowerConsumption, or a JSON boolean for Actuator).");
            }
            catch (ArgumentException ex)
            {
                return ApiProblem.Handled(logger, ex, StatusCodes.Status400BadRequest, "Submit reading", sensorId);
            }
            catch (Exception ex)
            {
                return ApiProblem.Unexpected(logger, ex, "Submit reading", sensorId);
            }
        });

        // 3. Retrieve every registered sensor with its latest reading.
        group.MapGet("/", (SensorRepository repo) =>
        {
            try
            {
                return Results.Ok(repo.GetAll());
            }
            catch (Exception ex)
            {
                return ApiProblem.Unexpected(logger, ex, "List sensors", subject: null);
            }
        });

        // 3b. Retrieve one sensor's stored reading history (the advanced-arrays
        // buffer's stage-2 List<T> conversion, for the ingestion UI to display).
        group.MapGet("/{sensorId}/history", (string sensorId, SensorRepository repo) =>
        {
            try
            {
                return repo.GetHistory(sensorId) is { } history
                    ? Results.Ok(history)
                    : ApiProblem.NotFound($"No sensor registered with id '{sensorId}'.");
            }
            catch (ArgumentException ex)
            {
                return ApiProblem.Handled(logger, ex, StatusCodes.Status400BadRequest, "Read sensor history", sensorId);
            }
            catch (Exception ex)
            {
                return ApiProblem.Unexpected(logger, ex, "Read sensor history", sensorId);
            }
        });

        // 3c. Retrieve one sensor's ingestion log (Fix B - COLLECTIONS): every
        // Stored reading plus recent Rejected attempts with their reason, as
        // tracked by that device's IngestionBuffer<T>.
        group.MapGet("/{sensorId}/ingestion-log", (string sensorId, SensorRepository repo) =>
        {
            try
            {
                return repo.GetIngestionLog(sensorId) is { } log
                    ? Results.Ok(log)
                    : ApiProblem.NotFound($"No sensor registered with id '{sensorId}'.");
            }
            catch (ArgumentException ex)
            {
                return ApiProblem.Handled(logger, ex, StatusCodes.Status400BadRequest, "Read ingestion log", sensorId);
            }
            catch (Exception ex)
            {
                return ApiProblem.Unexpected(logger, ex, "Read ingestion log", sensorId);
            }
        });

        // 4. Attach a sensor profile file: streamed straight from the multipart
        // request body to an AES-256-GCM-encrypted file on disk, section by
        // section and chunk by chunk, instead of buffering the whole upload
        // into an IFormFile (or even a whole plaintext file) first.
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
            AttachmentEncryptionKeyProvider encryptionKey,
            CancellationToken ct) =>
        {
            try
            {
                return await UploadProfileAsync(sensorId, request, repo, env, encryptionKey, logger, ct);
            }
            catch (BadHttpRequestException ex)
            {
                // Kestrel's own body limit or a malformed request stream.
                return ApiProblem.Handled(logger, ex, ex.StatusCode, "Upload profile file", sensorId);
            }
            catch (InvalidDataException ex)
            {
                // MultipartReader found malformed multipart framing or headers.
                return ApiProblem.Handled(logger, ex, StatusCodes.Status400BadRequest, "Upload profile file", sensorId);
            }
            catch (OperationCanceledException)
            {
                return ApiProblem.Cancelled(logger, "Upload profile file", sensorId);
            }
            catch (CryptographicException ex)
            {
                return ApiProblem.Unexpected(logger, ex, "Encrypt profile file", sensorId);
            }
            catch (IOException ex)
            {
                return ApiProblem.Unexpected(logger, ex, "Write profile file to disk", sensorId);
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiProblem.Unexpected(logger, ex, "Write profile file to disk", sensorId);
            }
            catch (Exception ex)
            {
                return ApiProblem.Unexpected(logger, ex, "Upload profile file", sensorId);
            }
        }).DisableAntiforgery();

        // 5. Download (or preview, for an image/text attachment) a sensor's
        // attached file: decrypted and streamed straight to the response body,
        // chunk by chunk, as it's read back off disk.
        group.MapGet("/{sensorId}/profile", async (
            string sensorId,
            HttpResponse response,
            SensorRepository repo,
            IWebHostEnvironment env,
            AttachmentEncryptionKeyProvider encryptionKey,
            CancellationToken ct) =>
        {
            try
            {
                if (repo.GetAttachment(sensorId) is not { } attachment)
                {
                    return ApiProblem.NotFound($"Sensor '{sensorId}' has no attachment.");
                }

                var sourcePath = Path.Combine(env.ContentRootPath, "data", attachment.StoredFileName);
                if (!File.Exists(sourcePath))
                {
                    return ApiProblem.NotFound("The attachment's stored file is missing.");
                }

                response.ContentType = attachment.Metadata.ContentType;
                response.Headers.ContentDisposition = $"inline; filename=\"{attachment.Metadata.OriginalFileName}\"";
                // Lets a caller compare what they actually received against the
                // plaintext hash recorded at upload time (see EncryptedAttachmentCodec).
                response.Headers["X-Content-Sha256"] = attachment.Metadata.Sha256Hex;

                await EncryptedAttachmentCodec.DecryptToStreamAsync(sourcePath, response.Body, encryptionKey.Key, ct);
                return Results.Empty;
            }
            catch (CryptographicException ex)
            {
                // A chunk's GCM tag didn't verify - the stored file was tampered
                // with or corrupted, or the configured key no longer matches the
                // one it was encrypted with. No chunk is ever written to the
                // response before its own tag passes, but earlier (good) chunks
                // may already be out on the wire by the time a later one fails -
                // once that's happened the status code can no longer change, so
                // the cleanest signal left is to stop writing and let the
                // response come up short, which a well-behaved client reports as
                // a failed/truncated download rather than trusting it.
                logger.LogError(ex, "Attachment for {SensorId} failed integrity verification.", sensorId);
                return DownloadFailed(response, "Stored attachment failed integrity verification and cannot be served.");
            }
            catch (InvalidDataException ex)
            {
                logger.LogError(ex, "Attachment for {SensorId} is not a valid encrypted envelope.", sensorId);
                return DownloadFailed(response, "Stored attachment is corrupt and cannot be served.");
            }
            catch (EndOfStreamException ex)
            {
                logger.LogError(ex, "Attachment for {SensorId} ended mid-chunk (truncated file).", sensorId);
                return DownloadFailed(response, "Stored attachment is truncated and cannot be served.");
            }
            catch (OperationCanceledException)
            {
                // The browser closed the download - nothing left to answer.
                logger.LogInformation("Download of the attachment for {SensorId} was cancelled by the client.", sensorId);
                return Results.Empty;
            }
            catch (IOException ex)
            {
                logger.LogError(ex, "Could not read the attachment for {SensorId} from disk.", sensorId);
                return DownloadFailed(response, "The attachment could not be read from storage.");
            }
            catch (ArgumentException ex)
            {
                return ApiProblem.Handled(logger, ex, StatusCodes.Status400BadRequest, "Download profile file", sensorId);
            }
            catch (Exception ex)
            {
                if (response.HasStarted)
                {
                    logger.LogError(ex, "Unexpected failure part-way through the download for {SensorId}.", sensorId);
                    return Results.Empty;
                }

                return ApiProblem.Unexpected(logger, ex, "Download profile file", sensorId);
            }
        });
    }

    // Code Attribution
    // Purpose: Pattern for reading a multipart request with MultipartReader
    //          section-by-section instead of IFormFile model binding, so the
    //          file content is never buffered into memory as a whole.
    // Author: Microsoft
    // Title: Upload files in ASP.NET Core - Upload large files with streaming
    // Year: 2026e
    // URL/DOI: https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads
    // Accessed: 17 October 2026
    private static async Task<IResult> UploadProfileAsync(
        string sensorId,
        HttpRequest request,
        SensorRepository repo,
        IWebHostEnvironment env,
        AttachmentEncryptionKeyProvider encryptionKey,
        ILogger logger,
        CancellationToken ct)
    {
        if (repo.GetSummary(sensorId) is null)
        {
            return ApiProblem.NotFound($"No sensor registered with id '{sensorId}'.");
        }

        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return ApiProblem.BadRequest("Expected a multipart/form-data request.");
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary))
        {
            return ApiProblem.BadRequest("Multipart boundary is missing.");
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
                return ApiProblem.BadRequest(
                    $"File type '{extension}' is not allowed. Allowed types: {FileSignature.AllowedExtensionsDisplay}.");
            }

            // Sniff just the first few bytes to confirm the content actually
            // matches the claimed type, before committing to write anything else.
            var header = new byte[FileSignature.SniffLength];
            var headerRead = await section.Body.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);

            if (!FileSignature.MatchesContent(extension, header.AsSpan(0, headerRead)))
            {
                return ApiProblem.BadRequest($"File content does not match its extension ('{extension}').");
            }

            var dataDirectory = Path.Combine(env.ContentRootPath, "data");
            Directory.CreateDirectory(dataDirectory);

            // sensorId comes from the caller, so it isn't trusted as a path
            // fragment (sensorId is often a MAC address, e.g. "AA:BB:CC:DD:EE:FF",
            // and ':' isn't valid in a Windows file name). A GUID segment keeps
            // repeated uploads for the same sensor from overwriting each other.
            // ".enc" marks this as the encrypted envelope, not the original file.
            var safeSensorId = SanitiseForFileName(sensorId);
            var storedFileName = $"{safeSensorId}_{Guid.NewGuid():N}.enc";
            var destinationPath = Path.Combine(dataDirectory, storedFileName);

            // 'header' is already consumed from section.Body (it was read for
            // the content sniff above); PrefixedStream puts those bytes back in
            // front of the rest of the section so the codec sees the file's
            // bytes in their original order, still without buffering them all.
            var plaintextStream = new PrefixedStream(header, headerRead, section.Body);

            EncryptedAttachmentCodec.EncryptResult encryptResult;
            try
            {
                encryptResult = await EncryptedAttachmentCodec.EncryptToFileAsync(
                    plaintextStream, destinationPath, encryptionKey.Key, MaxAttachmentSizeBytes, ct);
            }
            catch (Exception ex) when (ex is AttachmentTooLargeException or InvalidDataException)
            {
                // AttachmentTooLargeException is our own per-file check inside
                // the codec; InvalidDataException is MultipartReader's own
                // BodyLengthLimit tripping first - both mean the same thing to
                // the caller, so both get the same clean 400. Either way the
                // codec has already deleted its own partial output file.
                logger.LogWarning(ex, "Profile upload for {SensorId} rejected: over {LimitMb} MB.", sensorId, MaxAttachmentSizeBytes / (1024 * 1024));
                return ApiProblem.BadRequest(
                    $"File exceeds the maximum allowed size of {MaxAttachmentSizeBytes / (1024 * 1024)} MB.");
            }

            var metadata = new AttachmentMetadata(
                OriginalFileName: originalFileName,
                SizeBytes: encryptResult.PlaintextLength,
                ContentType: ContentTypeForExtension(extension),
                UploadedAt: DateTimeOffset.UtcNow,
                Sha256Hex: encryptResult.Sha256Hex);

            if (!repo.SetProfileFile(sensorId, storedFileName, metadata))
            {
                // The sensor vanished between the first check and now - don't
                // leave an orphaned encrypted file behind.
                TryDeleteFile(destinationPath, logger);
                return ApiProblem.NotFound($"No sensor registered with id '{sensorId}'.");
            }

            return Results.Ok(repo.GetSummary(sensorId));
        }

        return ApiProblem.BadRequest("No file part found in the request.");
    }

    /// <summary>
    /// A download failure: a ProblemDetails 500 if nothing has been sent yet,
    /// otherwise just stop writing so the client sees a truncated download.
    /// </summary>
    private static IResult DownloadFailed(HttpResponse response, string detail)
        => response.HasStarted
            ? Results.Empty
            : Results.Problem(detail, statusCode: StatusCodes.Status500InternalServerError, title: "Download failed");

    private static void TryDeleteFile(string path, ILogger logger)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete orphaned attachment file {Path}.", path);
        }
    }

    private static string ContentTypeForExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".json" => "application/json",
        ".csv" => "text/csv",
        _ => "text/plain",
    };

    private static string SanitiseForFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
