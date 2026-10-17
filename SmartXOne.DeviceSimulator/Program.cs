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

using System.Net.Http.Json;
using SmartXOne.Shared.Contracts;

var apiBaseUrl = GetArgValue(args, "--api") ?? "http://localhost:5080";
using var http = new HttpClient { BaseAddress = new Uri(apiBaseUrl) };

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
    await SendTelemetryAsync(http, fleet[0] with { Value = 12 }); // < 20% -> Critical
    Console.WriteLine($"[moisture-drop] Sent {fleet[0].DeviceId} = 12% (Critical - bypasses the standard queue).");
    ranOnDemandAction = true;
}

if (HasFlag(args, "--spike"))
{
    await SendTelemetryAsync(http, fleet[2] with { Value = 2600 }); // > 2000W -> Critical
    Console.WriteLine($"[spike] Sent {fleet[2].DeviceId} = 2600W (Critical - bypasses the standard queue).");
    ranOnDemandAction = true;
}

if (HasFlag(args, "--alert"))
{
    await SendTelemetryAsync(http, fleet[1] with { Value = 8 }); // < 20% -> Critical
    Console.WriteLine($"[alert] Sent {fleet[1].DeviceId} = 8% (Critical - the brief's 'high-priority alert').");
    ranOnDemandAction = true;
}

if (HasFlag(args, "--disconnect"))
{
    var targetId = GetArgValue(args, "--disconnect-device") ?? fleet[0].DeviceId;
    var response = await http.PostAsync($"api/realtime/devices/{Uri.EscapeDataString(targetId)}/disconnect", content: null);
    Console.WriteLine($"[disconnect] {targetId}: HTTP {(int)response.StatusCode} {response.StatusCode}");
    ranOnDemandAction = true;
}

if (HasFlag(args, "--once"))
{
    if (!ranOnDemandAction)
    {
        await SendFleetRoundAsync(http, fleet);
        Console.WriteLine("[once] Sent one round of telemetry for the whole fleet.");
    }

    return;
}

if (ranOnDemandAction)
{
    return; // on-demand flags without --once still exit after the triggered action(s)
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
        await SendFleetRoundAsync(http, fleet);
        await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);
    }
}
catch (OperationCanceledException)
{
    // Ctrl+C - expected.
}

Console.WriteLine("Simulator stopped.");

static async Task SendFleetRoundAsync(HttpClient http, List<SimulatedDevice> fleet)
{
    for (var i = 0; i < fleet.Count; i++)
    {
        var device = fleet[i] with { Value = NextValue(fleet[i]) };
        fleet[i] = device;
        await SendTelemetryAsync(http, device);
    }
}

/// <summary>A small random drift from the device's last value - plausible-looking, not pure noise (same idea as the client's MockTelemetryGenerator).</summary>
static double NextValue(SimulatedDevice device) => device.Kind switch
{
    RealtimeDeviceKind.EnvironmentalSensor => Math.Clamp(device.Value + (Random.Shared.NextDouble() * 6 - 3), 5, 95),
    RealtimeDeviceKind.SmartPlug => Math.Clamp(device.Value + Random.Shared.Next(-100, 101), 20, 3000),
    _ => device.Value,
};

static async Task SendTelemetryAsync(HttpClient http, SimulatedDevice device)
{
    var request = new SubmitTelemetryRequest(device.DeviceId, device.Kind, device.Value, device.Unit);
    var response = await http.PostAsJsonAsync("api/realtime/telemetry", request);
    if (!response.IsSuccessStatusCode)
    {
        Console.WriteLine($"  ! {device.DeviceId}: HTTP {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return;
    }

    Console.WriteLine($"  {device.DeviceId} ({device.Kind}) = {device.Value:0.#}{device.Unit}");
}

static bool HasFlag(string[] args, string flag) => args.Contains(flag, StringComparer.OrdinalIgnoreCase);

static string? GetArgValue(string[] args, string flag)
{
    var index = Array.FindIndex(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

internal sealed record SimulatedDevice(string DeviceId, RealtimeDeviceKind Kind, string Unit, double Value);
