using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Forms;
using SmartXOne.Shared.Contracts;

namespace SmartXOne.Client.Services;

/// <summary>Outcome of a mutating gateway call: either the parsed payload, or the error message the gateway returned.</summary>
public sealed record ApiResult<T>(bool Success, T? Value, string? Error)
{
    public static ApiResult<T> Ok(T value) => new(true, value, null);

    public static ApiResult<T> Fail(string error) => new(false, default, error);
}

/// <summary>
/// The one exception type every read in <see cref="SmartXApiClient"/> throws,
/// whatever actually went wrong underneath (network failure, timeout, a 4xx/5xx
/// ProblemDetails reply, or a body that wasn't the expected JSON). Pages catch
/// this, show <see cref="Exception.Message"/> (always written for a person to
/// read), and use <see cref="IsOffline"/> to decide between an "API offline"
/// banner and an ordinary error.
/// </summary>
public sealed class ApiException(string message, HttpStatusCode? statusCode, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>The HTTP status the gateway answered with, or <c>null</c> if it never answered.</summary>
    public HttpStatusCode? StatusCode { get; } = statusCode;

    /// <summary><c>true</c> when the gateway couldn't be reached at all (not started, crashed, network down).</summary>
    public bool IsOffline => StatusCode is null;
}

// Code Attribution
// Purpose: Distinguishing HttpRequestException (unreachable / non-success),
//          TaskCanceledException caused by HttpClient.Timeout, and caller
//          cancellation when handling HttpClient failures.
// Author: Microsoft
// Title: Make HTTP requests with the HttpClient - .NET
// Year: 2026
// URL/DOI: https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient
// Accessed: 10 November 2026

/// <summary>
/// Thin typed wrapper around <see cref="HttpClient"/> for talking to the gateway API.
/// Every page/component calls the API through this class instead of using HttpClient
/// directly, so the endpoint URLs, (de)serialisation and error handling live in one
/// place: reads throw <see cref="ApiException"/>, writes return an
/// <see cref="ApiResult{T}"/>, and neither ever lets a raw transport exception
/// reach a component.
/// </summary>
public sealed class SmartXApiClient(HttpClient http, ILogger<SmartXApiClient> logger)
{
    private const long MaxProfileFileBytes = 20 * 1024 * 1024;

    /// <summary>Shown whenever the gateway can't be reached at all.</summary>
    public const string OfflineMessage = "The gateway API is offline or unreachable. Start SmartXOne.Api (http://localhost:5080) and try again.";

    private readonly HttpClient _http = http;

    /// <summary>The gateway's base URL, for building a direct download/preview link to an attachment.</summary>
    public Uri BaseAddress => _http.BaseAddress!;

    /// <summary>
    /// Phase 1 round-trip check: fetch the gateway identity/health payload.
    /// </summary>
    /// <exception cref="ApiException">The gateway was unreachable or answered with an error.</exception>
    public Task<GatewayInfo> GetGatewayInfoAsync(CancellationToken ct = default)
        => GetAsync<GatewayInfo>("api/gateway/info", "Contacting the gateway", ct);

    /// <exception cref="ApiException">The gateway was unreachable or answered with an error.</exception>
    public Task<List<SensorSummary>> GetSensorsAsync(CancellationToken ct = default)
        => GetAsync<List<SensorSummary>>("api/sensors", "Loading sensors", ct);

    /// <summary>A sensor's stored reading history, oldest first (see <c>SensorRepository.GetHistory</c>).</summary>
    /// <exception cref="ApiException">The gateway was unreachable or answered with an error.</exception>
    public Task<List<TelemetryReadingDto>> GetSensorHistoryAsync(string sensorId, CancellationToken ct = default)
        => GetAsync<List<TelemetryReadingDto>>(
            $"api/sensors/{Uri.EscapeDataString(sensorId)}/history", $"Loading history for {sensorId}", ct);

    public Task<ApiResult<SensorSummary>> RegisterSensorAsync(RegisterSensorRequest request, CancellationToken ct = default)
        => PostForResultAsync<SensorSummary>("api/sensors", request, ct);

    /// <param name="sensorId">Id of an already-registered sensor.</param>
    /// <param name="value">
    /// A <c>float</c>, <c>int</c> or <c>bool</c> matching the sensor's category - it's
    /// declared as <c>object</c> here purely so one method can serialise whichever
    /// of the three the caller passes; <see cref="JsonSerializer"/> writes an
    /// object-typed value using its actual run-time type, so this still produces
    /// the plain JSON number/boolean the API's <c>SubmitReadingRequest</c> expects.
    /// </param>
    public Task<ApiResult<TelemetryReadingDto>> SubmitReadingAsync(string sensorId, object value, string? unit, CancellationToken ct = default)
        => PostForResultAsync<TelemetryReadingDto>(
            $"api/sensors/{Uri.EscapeDataString(sensorId)}/readings",
            new ReadingPayload(value, unit),
            ct);

    /// <param name="onProgress">
    /// Invoked with cumulative bytes sent as the upload streams, via
    /// <see cref="ProgressReportingStream"/> wrapping the browser file stream -
    /// this reflects bytes HttpClient has actually written to the request, not a
    /// simulated animation.
    /// </param>
    public async Task<ApiResult<SensorSummary>> UploadProfileAsync(
        string sensorId, IBrowserFile file, Action<long>? onProgress = null, CancellationToken ct = default)
    {
        try
        {
            using var content = new MultipartFormDataContent();
            await using var fileStream = file.OpenReadStream(MaxProfileFileBytes, ct);
            var progressStream = onProgress is null
                ? fileStream
                : new ProgressReportingStream(fileStream, onProgress);

            using var streamContent = new StreamContent(progressStream);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue(
                string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType);
            streamContent.Headers.ContentLength = file.Size;
            content.Add(streamContent, name: "file", fileName: file.Name);

            using var response = await _http.PostAsync($"api/sensors/{Uri.EscapeDataString(sensorId)}/profile", content, ct);
            return await ToResultAsync<SensorSummary>(response, ct);
        }
        catch (IOException ex)
        {
            // IBrowserFile.OpenReadStream throws this when the file is larger
            // than MaxProfileFileBytes, or the browser can't read it any more.
            logger.LogWarning(ex, "Could not read {FileName} for upload to {SensorId}.", file.Name, sensorId);
            return ApiResult<SensorSummary>.Fail($"Could not read '{file.Name}': {ex.Message}");
        }
        catch (Exception ex)
        {
            return Fail<SensorSummary>(ex, $"Uploading {file.Name} for {sensorId}", ct);
        }
    }

    private async Task<ApiResult<T>> PostForResultAsync<T>(string url, object body, CancellationToken ct)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(url, body, ct);
            return await ToResultAsync<T>(response, ct);
        }
        catch (Exception ex)
        {
            return Fail<T>(ex, $"POST {url}", ct);
        }
    }

    /// <summary>
    /// Turns a transport-level failure on a write into a readable
    /// <see cref="ApiResult{T}"/> failure, most specific exception type first.
    /// </summary>
    private ApiResult<T> Fail<T>(Exception ex, string operation, CancellationToken ct)
    {
        switch (ex)
        {
            case OperationCanceledException when ct.IsCancellationRequested:
                return ApiResult<T>.Fail("Cancelled.");

            case TaskCanceledException:
                // Not our token, so HttpClient.Timeout fired.
                logger.LogWarning(ex, "{Operation} timed out.", operation);
                return ApiResult<T>.Fail("The gateway did not respond in time. Please try again.");

            case HttpRequestException:
                logger.LogWarning(ex, "{Operation} failed: gateway unreachable.", operation);
                return ApiResult<T>.Fail(OfflineMessage);

            case JsonException or NotSupportedException:
                logger.LogError(ex, "{Operation}: response was not in the expected format.", operation);
                return ApiResult<T>.Fail("The gateway sent a response this page couldn't read.");

            default:
                logger.LogError(ex, "{Operation} failed unexpectedly.", operation);
                return ApiResult<T>.Fail($"Something went wrong: {ex.Message}");
        }
    }

    /// <summary>
    /// On success the gateway returns the requested payload as JSON; on a 4xx/5xx
    /// it returns an RFC 7807 ProblemDetails object (see <c>ApiProblem</c> in the
    /// API) whose <c>detail</c> is the human-readable reason.
    /// </summary>
    private async Task<ApiResult<T>> ToResultAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            try
            {
                var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
                return value is null ? ApiResult<T>.Fail("Gateway returned an empty response.") : ApiResult<T>.Ok(value);
            }
            catch (JsonException ex)
            {
                logger.LogError(ex, "Success response from {Url} wasn't valid JSON for {Type}.", response.RequestMessage?.RequestUri, typeof(T).Name);
                return ApiResult<T>.Fail("The gateway sent a response this page couldn't read.");
            }
        }

        return ApiResult<T>.Fail(await ReadErrorAsync(response, ct));
    }

    /// <summary>
    /// Pulls the most useful message out of an error response: ProblemDetails
    /// <c>detail</c>, then <c>title</c>, then a plain JSON string, and finally
    /// the bare status line if the body is none of those.
    /// </summary>
    private async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var fallback = $"Request failed ({(int)response.StatusCode} {response.ReasonPhrase}).";

        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body))
            {
                return fallback;
            }

            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;

            if (root.ValueKind == JsonValueKind.String)
            {
                return root.GetString() ?? fallback;
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                {
                    return detail.GetString() ?? fallback;
                }

                if (root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                {
                    return title.GetString() ?? fallback;
                }
            }

            return fallback;
        }
        catch (JsonException)
        {
            return fallback; // body wasn't JSON (e.g. a proxy's HTML error page)
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            logger.LogWarning(ex, "Could not read the error body for {Status}.", (int)response.StatusCode);
            return fallback;
        }
    }

    /// <summary>
    /// Shared GET path for every read: success deserialises to <typeparamref name="T"/>,
    /// anything else becomes an <see cref="ApiException"/> with a readable message.
    /// Caller cancellation is the one exception left as-is, so a component being
    /// disposed mid-request doesn't show an error for something nobody is waiting on.
    /// </summary>
    private async Task<T> GetAsync<T>(string url, string operation, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(url, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            logger.LogWarning(ex, "{Operation} timed out.", operation);
            throw new ApiException($"{operation}: the gateway did not respond in time.", HttpStatusCode.RequestTimeout, ex);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "{Operation} failed: gateway unreachable.", operation);
            throw new ApiException(OfflineMessage, statusCode: null, ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorAsync(response, ct);
                logger.LogWarning("{Operation} failed with {Status}: {Error}", operation, (int)response.StatusCode, error);
                throw new ApiException($"{operation}: {error}", response.StatusCode);
            }

            try
            {
                return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
                    ?? throw new ApiException($"{operation}: the gateway returned an empty response.", response.StatusCode);
            }
            catch (JsonException ex)
            {
                logger.LogError(ex, "{Operation}: response was not valid JSON for {Type}.", operation, typeof(T).Name);
                throw new ApiException($"{operation}: the gateway sent data this page couldn't read.", response.StatusCode, ex);
            }
            catch (NotSupportedException ex)
            {
                // Content-Type wasn't JSON at all.
                logger.LogError(ex, "{Operation}: response had an unsupported content type.", operation);
                throw new ApiException($"{operation}: the gateway sent data this page couldn't read.", response.StatusCode, ex);
            }
        }
    }

    /// <exception cref="ApiException">The gateway was unreachable or answered with an error.</exception>
    public Task<List<IssueDto>> GetIssuesAsync(IssueStatus? status = null, IssueSeverity? severity = null, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (status is { } s) query.Add($"status={s}");
        if (severity is { } sev) query.Add($"severity={sev}");
        var url = "api/issues" + (query.Count > 0 ? "?" + string.Join('&', query) : "");

        return GetAsync<List<IssueDto>>(url, "Loading issues", ct);
    }

    public Task<ApiResult<IssueDto>> CreateIssueAsync(CreateIssueRequest request, CancellationToken ct = default)
        => PostForResultAsync<IssueDto>("api/issues", request, ct);

    public Task<ApiResult<IssueDto>> AcknowledgeIssueAsync(string id, CancellationToken ct = default)
        => PostForResultAsync<IssueDto>($"api/issues/{Uri.EscapeDataString(id)}/acknowledge", new { }, ct);

    public Task<ApiResult<IssueDto>> ResolveIssueAsync(string id, CancellationToken ct = default)
        => PostForResultAsync<IssueDto>($"api/issues/{Uri.EscapeDataString(id)}/resolve", new { }, ct);

    // --- Part 2: Real-Time Command Stream ---------------------------------

    /// <exception cref="ApiException">The gateway was unreachable or answered with an error.</exception>
    public Task<List<DeviceDto>> GetRealtimeDevicesAsync(CancellationToken ct = default)
        => GetAsync<List<DeviceDto>>("api/realtime/devices", "Loading devices", ct);

    /// <exception cref="ApiException">The gateway was unreachable or answered with an error.</exception>
    public Task<List<TelemetryPointDto>> GetDeviceTimelineAsync(string deviceId, CancellationToken ct = default)
        => GetAsync<List<TelemetryPointDto>>(
            $"api/realtime/devices/{Uri.EscapeDataString(deviceId)}/timeline", $"Loading the timeline for {deviceId}", ct);

    /// <exception cref="ApiException">The gateway was unreachable or answered with an error.</exception>
    public Task<List<DeviceCommandDto>> GetRecentCommandsAsync(CancellationToken ct = default)
        => GetAsync<List<DeviceCommandDto>>("api/realtime/commands", "Loading command history", ct);

    /// <exception cref="ApiException">The gateway was unreachable or answered with an error.</exception>
    public Task<QueueStatusDto> GetQueueStatusAsync(CancellationToken ct = default)
        => GetAsync<QueueStatusDto>("api/realtime/queue-status", "Loading queue status", ct);

    public Task<ApiResult<DeviceDto>> IssueCommandAsync(string deviceId, DeviceCommandType type, CancellationToken ct = default)
        => PostForResultAsync<DeviceDto>($"api/realtime/devices/{Uri.EscapeDataString(deviceId)}/commands", new IssueCommandRequest(type), ct);

    public Task<ApiResult<object>> ShutAllValvesAsync(CancellationToken ct = default)
        => PostForResultAsync<object>("api/realtime/commands/shut-all-valves", new { }, ct);

    public Task<ApiResult<UndoResultDto>> UndoCommandAsync(CancellationToken ct = default)
        => PostForResultAsync<UndoResultDto>("api/realtime/commands/undo", new { }, ct);

    public Task<ApiResult<object>> DisconnectDeviceAsync(string deviceId, CancellationToken ct = default)
        => PostForResultAsync<object>($"api/realtime/devices/{Uri.EscapeDataString(deviceId)}/disconnect", new { }, ct);

    // --- Part 2: Predictive Action / Recommendation Engine ----------------

    /// <exception cref="ApiException">The gateway was unreachable or answered with an error.</exception>
    public Task<List<SuggestionDto>> GetSuggestionsAsync(CancellationToken ct = default)
        => GetAsync<List<SuggestionDto>>("api/recommendations/suggestions", "Loading suggestions", ct);

    /// <summary>
    /// Call this debounced (not on every keystroke) - each call is one "search"
    /// event logged for the recommendation engine. Fire-and-forget by design
    /// (the endpoint returns 202 with no body, and a failed log shouldn't
    /// interrupt the UI), so this doesn't go through PostForResultAsync/
    /// ApiResult&lt;T&gt; like the rest of this class - there's nothing meaningful
    /// to parse from an empty Accepted response. A failure is still logged to
    /// the browser console, just not shown to the user.
    /// </summary>
    public async Task LogSearchAsync(string term, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync("api/recommendations/events/search", new LogSearchRequest(term), ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Search '{Term}' was not logged: gateway answered {Status}.", term, (int)response.StatusCode);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Superseded by a newer search - nothing to log.
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Search '{Term}' was not logged: gateway unreachable.", term);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Search '{Term}' was not logged.", term);
        }
    }

    // --- Part 3: Network Topology and Mesh Routing ------------------------

    /// <exception cref="ApiException">The gateway was unreachable or answered with an error.</exception>
    public Task<TopologyDto> GetTopologyAsync(CancellationToken ct = default)
        => GetAsync<TopologyDto>("api/topology", "Loading the mesh topology", ct);

    /// <exception cref="ApiException">The gateway was unreachable or answered with an error.</exception>
    public Task<SpanningTreeDto> GetSpanningTreeAsync(CancellationToken ct = default)
        => GetAsync<SpanningTreeDto>("api/topology/mst", "Loading the spanning tree", ct);

    /// <exception cref="ApiException">The gateway was unreachable, the node doesn't exist (404), or another error occurred.</exception>
    public Task<MeshRouteDto> GetMeshRouteAsync(string mac, CancellationToken ct = default)
        => GetAsync<MeshRouteDto>($"api/topology/route/{Uri.EscapeDataString(mac)}", $"Finding the route for {mac}", ct);

    /// <param name="from">A node MAC or subnet name; <c>null</c> for the gateway.</param>
    /// <exception cref="ApiException">The gateway was unreachable, the start doesn't exist (404), or another error occurred.</exception>
    public Task<ReachabilityDto> GetReachabilityAsync(string? from, CancellationToken ct = default)
        => GetAsync<ReachabilityDto>(
            string.IsNullOrWhiteSpace(from) ? "api/topology/reachable" : $"api/topology/reachable?from={Uri.EscapeDataString(from)}",
            $"Checking reachability from {from ?? "the gateway"}", ct);

    /// <exception cref="ApiException">The gateway was unreachable or answered with an error.</exception>
    public Task<List<MeshComponentDto>> GetMeshComponentsAsync(CancellationToken ct = default)
        => GetAsync<List<MeshComponentDto>>("api/topology/components", "Loading mesh components", ct);

    public Task<ApiResult<MeshNodeDto>> SetMeshNodeStateAsync(string mac, SetMeshNodeStateRequest request, CancellationToken ct = default)
        => SendForResultAsync<MeshNodeDto>(HttpMethod.Put, $"api/topology/nodes/{Uri.EscapeDataString(mac)}/state", request, ct);

    // --- Part 3: Telemetry history (AVL range queries) --------------------

    /// <exception cref="ApiException">The gateway was unreachable or answered with an error.</exception>
    public Task<List<HistoryDeviceDto>> GetHistoryDevicesAsync(CancellationToken ct = default)
        => GetAsync<List<HistoryDeviceDto>>("api/history", "Loading devices with history", ct);

    /// <summary>Readings for one device between <paramref name="from"/> and <paramref name="to"/> (either optional), optionally one metric.</summary>
    /// <exception cref="ApiException">The gateway was unreachable, the device has no history (404), the range is invalid (400), or another error occurred.</exception>
    public Task<TelemetryHistoryDto> GetTelemetryHistoryAsync(
        string deviceId, DateTimeOffset? from, DateTimeOffset? to, string? metric, int? limit = null, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (from is { } f)
        {
            query.Add($"from={Uri.EscapeDataString(f.ToString("O"))}");
        }

        if (to is { } t)
        {
            query.Add($"to={Uri.EscapeDataString(t.ToString("O"))}");
        }

        if (!string.IsNullOrWhiteSpace(metric))
        {
            query.Add($"metric={Uri.EscapeDataString(metric)}");
        }

        if (limit is { } l)
        {
            query.Add($"limit={l}");
        }

        var url = $"api/history/{Uri.EscapeDataString(deviceId)}" + (query.Count == 0 ? "" : "?" + string.Join('&', query));
        return GetAsync<TelemetryHistoryDto>(url, $"Loading history for {deviceId}", ct);
    }

    private async Task<ApiResult<T>> SendForResultAsync<T>(HttpMethod method, string url, object body, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
            using var response = await _http.SendAsync(request, ct);
            return await ToResultAsync<T>(response, ct);
        }
        catch (Exception ex)
        {
            return Fail<T>(ex, $"{method} {url}", ct);
        }
    }

    private sealed record ReadingPayload(object Value, string? Unit);
}
