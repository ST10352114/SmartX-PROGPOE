// SmartXOne.DeviceSimulator - stands in for real ESP32 sensor nodes and smart
// plugs: it sends genuine HTTP POSTs to the gateway's Part 2 endpoints
// (/api/realtime/telemetry, /api/realtime/devices/{id}/disconnect) exactly as
// hardware would, so the queue/priority-queue/registry/command-stream code
// runs against real traffic instead of only unit-style calls.
//
// Usage:
//   dotnet run                          continuous fleet simulation (Ctrl+C to stop)
//   dotnet run -- --once                one simulation round, then exit
//   dotnet run -- --spike               send one severe power-spike reading (Critical)
//   dotnet run -- --moisture-drop       send one moisture-under-20% reading (Critical)
//   dotnet run -- --alert               send one generic high-priority alert
//   dotnet run -- --disconnect          send a disconnect signal for one device
//   dotnet run -- --api http://host:port  override the gateway's base URL
//
// Code Attribution
// Purpose: Handling HttpRequestException (gateway unreachable) and
//          TaskCanceledException (timeout) around every HttpClient call, so one
//          failed send is reported and the simulator keeps running.
// Author: Microsoft
// Title: Make HTTP requests with the HttpClient - .NET
// Year: 2026
// URL/DOI: https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient
// Accessed: 10 November 2026

using System.Net.Http.Json;
using System.Text.Json;
using SmartXOne.Shared.Contracts;

var apiBaseUrl = GetArgValue(args, "--api") ?? "http://localhost:5080";

Uri apiBaseUri;
try
{
    apiBaseUri = new Uri(apiBaseUrl, UriKind.Absolute);
}
catch (UriFormatException ex)
{
    Console.Error.WriteLine($"--api value '{apiBaseUrl}' is not a valid absolute URL ({ex.Message}). Example: --api http://localhost:5080");
    return 1;
}

using var http = new HttpClient { BaseAddress = apiBaseUri, Timeout = TimeSpan.FromSeconds(10) };

var fleet = new List<SimulatedDevice>
{
    new("ESP32-ENV-01", RealtimeDeviceKind.EnvironmentalSensor, "%", 55),
    new("ESP32-ENV-02", RealtimeDeviceKind.EnvironmentalSensor, "%", 48),
    new("PLUG-POWER-01", RealtimeDeviceKind.SmartPlug, "W", 800),
    new("PLUG-POWER-02", RealtimeDeviceKind.SmartPlug, "W", 650),
};

Console.WriteLine($"SmartX device simulator -> {apiBaseUrl}");

var ranOnDemandAction = false;

if (HasFlag(args, "--moisture-drop"))
{
    if (await SendTelemetryAsync(http, fleet[0] with { Value = 12 })) // < 20% -> Critical
    {
        Console.WriteLine($"[moisture-drop] Sent {fleet[0].DeviceId} = 12% (Critical - bypasses the standard queue).");
    }

    ranOnDemandAction = true;
}

if (HasFlag(args, "--spike"))
{
    if (await SendTelemetryAsync(http, fleet[2] with { Value = 2600 })) // > 2000W -> Critical
    {
        Console.WriteLine($"[spike] Sent {fleet[2].DeviceId} = 2600W (Critical - bypasses the standard queue).");
    }

    ranOnDemandAction = true;
}

if (HasFlag(args, "--alert"))
{
    if (await SendTelemetryAsync(http, fleet[1] with { Value = 8 })) // < 20% -> Critical
    {
        Console.WriteLine($"[alert] Sent {fleet[1].DeviceId} = 8% (Critical - the brief's 'high-priority alert').");
    }

    ranOnDemandAction = true;
}

if (HasFlag(args, "--disconnect"))
{
    var targetId = GetArgValue(args, "--disconnect-device") ?? fleet[0].DeviceId;
    try
    {
        using var response = await http.PostAsync($"api/realtime/devices/{Uri.EscapeDataString(targetId)}/disconnect", content: null);
        Console.WriteLine($"[disconnect] {targetId}: HTTP {(int)response.StatusCode} {response.StatusCode}");
    }
    catch (HttpRequestException ex)
    {
        Console.WriteLine($"[disconnect] {targetId}: gateway unreachable at {apiBaseUrl} ({ex.Message}).");
    }
    catch (TaskCanceledException)
    {
        Console.WriteLine($"[disconnect] {targetId}: gateway did not respond within {http.Timeout.TotalSeconds:0}s.");
    }

    ranOnDemandAction = true;
}

if (HasFlag(args, "--once"))
{
    if (!ranOnDemandAction)
    {
        var accepted = await SendFleetRoundAsync(http, fleet);
        Console.WriteLine($"[once] Sent one round of telemetry: {accepted}/{fleet.Count} devices accepted.");
    }

    return 0;
}

if (ranOnDemandAction)
{
    return 0; // on-demand flags without --once still exit after the triggered action(s)
}

Console.WriteLine($"Simulating {fleet.Count} devices every 3s. Press Ctrl+C to stop.");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    while (!cts.IsCancellationRequested)
    {
        // A failed send (gateway restarting, network blip) is reported inside
        // SendTelemetryAsync and the loop simply tries again on the next tick.
        await SendFleetRoundAsync(http, fleet);
        await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);
    }
}
catch (OperationCanceledException)
{
    // Ctrl+C - expected.
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Simulator stopped unexpectedly: {ex.GetType().Name}: {ex.Message}");
    return 1;
}

Console.WriteLine("Simulator stopped.");
return 0;

/// <returns>How many devices in the round the gateway accepted.</returns>
static async Task<int> SendFleetRoundAsync(HttpClient http, List<SimulatedDevice> fleet)
{
    var accepted = 0;
    for (var i = 0; i < fleet.Count; i++)
    {
        var device = fleet[i] with { Value = NextValue(fleet[i]) };
        fleet[i] = device;
        if (await SendTelemetryAsync(http, device))
        {
            accepted++;
        }
    }

    return accepted;
}

/// <summary>A small random drift from the device's last value - plausible-looking, not pure noise (same idea as the client's MockTelemetryGenerator).</summary>
static double NextValue(SimulatedDevice device) => device.Kind switch
{
    RealtimeDeviceKind.EnvironmentalSensor => Math.Clamp(device.Value + (Random.Shared.NextDouble() * 6 - 3), 5, 95),
    RealtimeDeviceKind.SmartPlug => Math.Clamp(device.Value + Random.Shared.Next(-100, 101), 20, 3000),
    _ => device.Value,
};

/// <returns><c>true</c> if the gateway accepted the reading. Failures are reported to the console rather than thrown, so one bad send never ends the run.</returns>
static async Task<bool> SendTelemetryAsync(HttpClient http, SimulatedDevice device)
{
    try
    {
        var request = new SubmitTelemetryRequest(device.DeviceId, device.Kind, device.Value, device.Unit);
        using var response = await http.PostAsJsonAsync("api/realtime/telemetry", request);
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"  ! {device.DeviceId}: HTTP {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            return false;
        }

        Console.WriteLine($"  {device.DeviceId} ({device.Kind}) = {device.Value:0.#}{device.Unit}");
        return true;
    }
    catch (HttpRequestException ex)
    {
        Console.WriteLine($"  ! {device.DeviceId}: gateway unreachable at {http.BaseAddress} ({ex.Message}) - is SmartXOne.Api running?");
    }
    catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
    {
        Console.WriteLine($"  ! {device.DeviceId}: gateway did not respond within {http.Timeout.TotalSeconds:0}s.");
    }
    catch (JsonException ex)
    {
        Console.WriteLine($"  ! {device.DeviceId}: reading could not be serialised ({ex.Message}).");
    }

    return false;
}

static bool HasFlag(string[] args, string flag) => args.Contains(flag, StringComparer.OrdinalIgnoreCase);

static string? GetArgValue(string[] args, string flag)
{
    var index = Array.FindIndex(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

internal sealed record SimulatedDevice(string DeviceId, RealtimeDeviceKind Kind, string Unit, double Value);
