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
/// Thin typed wrapper around <see cref="HttpClient"/> for talking to the gateway API.
/// Every page/component calls the API through this class instead of using HttpClient
/// directly, so the endpoint URLs and (de)serialisation live in one place.
/// </summary>
public sealed class SmartXApiClient(HttpClient http)
{
    private const long MaxProfileFileBytes = 20 * 1024 * 1024;

    private readonly HttpClient _http = http;

    /// <summary>The gateway's base URL, for building a direct download/preview link to an attachment.</summary>
    public Uri BaseAddress => _http.BaseAddress!;

    /// <summary>
    /// Phase 1 round-trip check: fetch the gateway identity/health payload.
    /// </summary>
    public async Task<GatewayInfo?> GetGatewayInfoAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<GatewayInfo>("api/gateway/info", ct);

    public async Task<List<SensorSummary>> GetSensorsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<SensorSummary>>("api/sensors", ct) ?? [];

    /// <summary>A sensor's stored reading history, oldest first (see <c>SensorRepository.GetHistory</c>).</summary>
    public async Task<List<TelemetryReadingDto>> GetSensorHistoryAsync(string sensorId, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<TelemetryReadingDto>>(
            $"api/sensors/{Uri.EscapeDataString(sensorId)}/history", ct) ?? [];

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

            var response = await _http.PostAsync($"api/sensors/{Uri.EscapeDataString(sensorId)}/profile", content, ct);
            return await ToResultAsync<SensorSummary>(response, ct);
        }
        catch (OperationCanceledException)
        {
            return ApiResult<SensorSummary>.Fail("Upload cancelled.");
        }
        catch (Exception ex)
        {
            return ApiResult<SensorSummary>.Fail(ex.Message);
        }
    }

    private async Task<ApiResult<T>> PostForResultAsync<T>(string url, object body, CancellationToken ct)
    {
        try
        {
            var response = await _http.PostAsJsonAsync(url, body, ct);
            return await ToResultAsync<T>(response, ct);
        }
        catch (Exception ex)
        {
            return ApiResult<T>.Fail(ex.Message);
        }
    }

    /// <summary>
    /// On success the gateway returns the requested payload as JSON; on a 4xx it
    /// returns a plain JSON string (the error message) - see the
    /// <c>Results.BadRequest("...")</c> / <c>Results.Conflict("...")</c> /
    /// <c>Results.NotFound("...")</c> calls in <c>SensorEndpoints.cs</c>.
    /// </summary>
    private static async Task<ApiResult<T>> ToResultAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
            return value is null ? ApiResult<T>.Fail("Gateway returned an empty response.") : ApiResult<T>.Ok(value);
        }

        string? error;
        try
        {
            error = await response.Content.ReadFromJsonAsync<string>(cancellationToken: ct);
        }
        catch (JsonException)
        {
            error = null;
        }

        return ApiResult<T>.Fail(error ?? $"Request failed ({(int)response.StatusCode} {response.ReasonPhrase}).");
    }

    public async Task<List<IssueDto>> GetIssuesAsync(IssueStatus? status = null, IssueSeverity? severity = null, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (status is { } s) query.Add($"status={s}");
        if (severity is { } sev) query.Add($"severity={sev}");
        var url = "api/issues" + (query.Count > 0 ? "?" + string.Join('&', query) : "");

        return await _http.GetFromJsonAsync<List<IssueDto>>(url, ct) ?? [];
    }

    public Task<ApiResult<IssueDto>> CreateIssueAsync(CreateIssueRequest request, CancellationToken ct = default)
        => PostForResultAsync<IssueDto>("api/issues", request, ct);

    public Task<ApiResult<IssueDto>> AcknowledgeIssueAsync(string id, CancellationToken ct = default)
        => PostForResultAsync<IssueDto>($"api/issues/{Uri.EscapeDataString(id)}/acknowledge", new { }, ct);

    public Task<ApiResult<IssueDto>> ResolveIssueAsync(string id, CancellationToken ct = default)
        => PostForResultAsync<IssueDto>($"api/issues/{Uri.EscapeDataString(id)}/resolve", new { }, ct);

    // --- Part 2: Real-Time Command Stream ---------------------------------

    public async Task<List<DeviceDto>> GetRealtimeDevicesAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<DeviceDto>>("api/realtime/devices", ct) ?? [];

    public async Task<List<TelemetryPointDto>> GetDeviceTimelineAsync(string deviceId, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<TelemetryPointDto>>(
            $"api/realtime/devices/{Uri.EscapeDataString(deviceId)}/timeline", ct) ?? [];

    public async Task<List<DeviceCommandDto>> GetRecentCommandsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<DeviceCommandDto>>("api/realtime/commands", ct) ?? [];

    public async Task<QueueStatusDto?> GetQueueStatusAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<QueueStatusDto>("api/realtime/queue-status", ct);

    public Task<ApiResult<DeviceDto>> IssueCommandAsync(string deviceId, DeviceCommandType type, CancellationToken ct = default)
        => PostForResultAsync<DeviceDto>($"api/realtime/devices/{Uri.EscapeDataString(deviceId)}/commands", new IssueCommandRequest(type), ct);

    public Task<ApiResult<object>> ShutAllValvesAsync(CancellationToken ct = default)
        => PostForResultAsync<object>("api/realtime/commands/shut-all-valves", new { }, ct);

    public Task<ApiResult<UndoResultDto>> UndoCommandAsync(CancellationToken ct = default)
        => PostForResultAsync<UndoResultDto>("api/realtime/commands/undo", new { }, ct);

    public Task<ApiResult<object>> DisconnectDeviceAsync(string deviceId, CancellationToken ct = default)
        => PostForResultAsync<object>($"api/realtime/devices/{Uri.EscapeDataString(deviceId)}/disconnect", new { }, ct);

    // --- Part 2: Predictive Action / Recommendation Engine ----------------

    public async Task<List<SuggestionDto>> GetSuggestionsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<SuggestionDto>>("api/recommendations/suggestions", ct) ?? [];

    /// <summary>
    /// Call this debounced (not on every keystroke) - each call is one "search"
    /// event logged for the recommendation engine. Fire-and-forget by design
    /// (the endpoint returns 202 with no body, and a failed log shouldn't
    /// interrupt the UI), so this doesn't go through PostForResultAsync/
    /// ApiResult&lt;T&gt; like the rest of this class - there's nothing meaningful
    /// to parse from an empty Accepted response.
    /// </summary>
    public async Task LogSearchAsync(string term, CancellationToken ct = default)
    {
        try
        {
            await _http.PostAsJsonAsync("api/recommendations/events/search", new LogSearchRequest(term), ct);
        }
        catch
        {
            // Best-effort telemetry.
        }
    }

    private sealed record ReadingPayload(object Value, string? Unit);
}
