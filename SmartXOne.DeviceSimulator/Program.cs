// SmartXOne.DeviceSimulator - stands in for a real mesh of ESP32 sensor nodes,
// smart plugs and valve actuators: it sends genuine HTTP requests to the
// gateway exactly as hardware would, so the topology, queue/priority-queue,
// registry, history and command-stream code runs against real traffic.
//
// On start-up it registers a 13-node mesh with the gateway (GW-00 plus three
// subnets: Greenhouse A, Greenhouse B and Pump house), then every node except
// the gateway reports telemetry under its MAC address.
//
// Usage:
//   dotnet run                          continuous mesh simulation; press a node's key to toggle it offline/online, q to quit
//   dotnet run -- --once                register the mesh, send one round, then exit
//   dotnet run -- --spike               send one severe power-spike reading (Critical)
//   dotnet run -- --moisture-drop       send one moisture-under-20% reading (Critical)
//   dotnet run -- --alert               send one generic high-priority alert
//   dotnet run -- --disconnect          send a disconnect signal for one node (--disconnect-device <mac> to pick)
//   dotnet run -- --node-offline <mac>  take a mesh node offline (MAC or name, e.g. GHA-01)
//   dotnet run -- --node-online <mac>   bring a mesh node back online
//   dotnet run -- --api http://host:port  override the gateway's base URL
//
// Code Attribution
// Purpose: Handling HttpRequestException (gateway unreachable) and
//          TaskCanceledException (timeout) around every HttpClient call, so one
//          failed send is reported and the simulator keeps running.
// Author: Microsoft
// Title: Make HTTP requests with the HttpClient - .NET
// Year: 2026d
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

var mesh = MeshPlan.Build();
var fleet = mesh.Nodes.Where(n => !n.IsGateway).ToList();
var sensors = fleet.Where(n => n.Kind == RealtimeDeviceKind.EnvironmentalSensor).ToList();
var plugs = fleet.Where(n => n.Kind == RealtimeDeviceKind.SmartPlug).ToList();

Console.WriteLine($"SmartX mesh simulator -> {apiBaseUrl}");

var ranOnDemandAction = false;

if (HasFlag(args, "--moisture-drop"))
{
    if (await SendReadingAsync(http, sensors[0], TelemetryMetrics.Moisture, 12, "%", verbose: true) == SendOutcome.Accepted) // < 20% -> Critical
    {
        Console.WriteLine($"[moisture-drop] Sent {sensors[0].Name} ({sensors[0].Mac}) = 12% (Critical - bypasses the standard queue).");
    }

    ranOnDemandAction = true;
}

if (HasFlag(args, "--spike"))
{
    if (await SendReadingAsync(http, plugs[0], TelemetryMetrics.Power, 2600, "W", verbose: true) == SendOutcome.Accepted) // > 2000W -> Critical
    {
        Console.WriteLine($"[spike] Sent {plugs[0].Name} ({plugs[0].Mac}) = 2600W (Critical - bypasses the standard queue).");
    }

    ranOnDemandAction = true;
}

if (HasFlag(args, "--alert"))
{
    if (await SendReadingAsync(http, sensors[1], TelemetryMetrics.Moisture, 8, "%", verbose: true) == SendOutcome.Accepted) // < 20% -> Critical
    {
        Console.WriteLine($"[alert] Sent {sensors[1].Name} ({sensors[1].Mac}) = 8% (Critical - the brief's 'high-priority alert').");
    }

    ranOnDemandAction = true;
}

if (HasFlag(args, "--disconnect"))
{
    var target = mesh.Resolve(GetArgValue(args, "--disconnect-device")) ?? sensors[0];
    await SendDisconnectAsync(http, target);
    ranOnDemandAction = true;
}

foreach (var (flag, online) in new[] { ("--node-offline", false), ("--node-online", true) })
{
    if (!HasFlag(args, flag))
    {
        continue;
    }

    var value = GetArgValue(args, flag);
    var node = mesh.Resolve(value);
    if (node is null)
    {
        Console.Error.WriteLine($"[{flag.TrimStart('-')}] '{value}' is not a mesh node. Known nodes: {string.Join(", ", mesh.Nodes.Select(n => n.Name))}");
        return 1;
    }

    // Make sure the gateway knows the mesh first (registration is idempotent).
    if (await RegisterMeshAsync(http, mesh))
    {
        await SetNodeOnlineAsync(http, node, online);
    }

    ranOnDemandAction = true;
}

if (HasFlag(args, "--once"))
{
    if (!ranOnDemandAction)
    {
        await RegisterMeshAsync(http, mesh);
        var (accepted, attempted) = await SendMeshRoundAsync(http, mesh, verbose: true);
        Console.WriteLine($"[once] Sent one round of telemetry: {accepted}/{attempted} readings accepted.");
    }

    return 0;
}

if (ranOnDemandAction)
{
    return 0; // on-demand flags without --once still exit after the triggered action(s)
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

PrintKeyMap(mesh);
var keyTask = Console.IsInputRedirected ? Task.CompletedTask : KeyLoopAsync(http, mesh, cts);

try
{
    var registered = false;
    var round = 0;
    while (!cts.IsCancellationRequested)
    {
        // Retried every tick until it succeeds, so starting the simulator
        // before the API (or restarting the API) still ends with a full mesh.
        registered = registered || await RegisterMeshAsync(http, mesh);

        var (accepted, attempted) = await SendMeshRoundAsync(http, mesh, verbose: false);
        round++;
        var offline = mesh.OfflineNames();
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] round {round}: {accepted}/{attempted} readings accepted"
                          + (offline.Count == 0 ? "" : $" - offline: {string.Join(", ", offline)}"));

        await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);
    }
}
catch (OperationCanceledException)
{
    // Ctrl+C or q - expected.
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Simulator stopped unexpectedly: {ex.GetType().Name}: {ex.Message}");
    return 1;
}

try
{
    await keyTask;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Keyboard listener failed: {ex.Message}");
}

Console.WriteLine("Simulator stopped.");
return 0;

static void PrintKeyMap(MeshPlan mesh)
{
    Console.WriteLine($"Simulating {mesh.Nodes.Count} mesh nodes, telemetry every 3s.");
    Console.WriteLine("Press a key to toggle a node offline/online, q (or Ctrl+C) to quit:");
    foreach (var node in mesh.Nodes.Where(n => n.Key is not null))
    {
        Console.WriteLine($"  [{node.Key}] {node.Name,-7} {node.Mac}  {node.Subnet,-12} {node.Kind}");
    }
}

/// <summary>Polls the keyboard so it can stop on cancellation; a node's key toggles it, q quits.</summary>
static async Task KeyLoopAsync(HttpClient http, MeshPlan mesh, CancellationTokenSource cts)
{
    try
    {
        while (!cts.IsCancellationRequested)
        {
            if (!Console.KeyAvailable)
            {
                await Task.Delay(100, cts.Token);
                continue;
            }

            var key = char.ToLowerInvariant(Console.ReadKey(intercept: true).KeyChar);
            if (key == 'q')
            {
                Console.WriteLine("Quitting...");
                await cts.CancelAsync();
                return;
            }

            var node = mesh.Nodes.FirstOrDefault(n => n.Key == key);
            if (node is null)
            {
                Console.WriteLine($"  (no node on key '{key}')");
                continue;
            }

            await SetNodeOnlineAsync(http, node, !node.IsOnline);
        }
    }
    catch (OperationCanceledException)
    {
        // Shutting down - expected.
    }
    catch (InvalidOperationException ex)
    {
        // No usable console (e.g. started from an IDE without one): keep simulating without key toggles.
        Console.WriteLine($"Key toggles unavailable ({ex.Message}); use --node-offline / --node-online instead.");
    }
}

/// <summary>Registers every node and link. Idempotent on the API side, so it's safe to repeat.</summary>
/// <returns><c>true</c> when the whole mesh was accepted.</returns>
static async Task<bool> RegisterMeshAsync(HttpClient http, MeshPlan mesh)
{
    try
    {
        foreach (var node in mesh.Nodes)
        {
            var request = new RegisterMeshNodeRequest(node.Mac, node.Name, node.Subnet, node.IsGateway, node.X, node.Y, (int)Math.Round(node.Battery));
            using var response = await http.PostAsJsonAsync("api/topology/nodes", request);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"  ! register {node.Name}: HTTP {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
                return false;
            }
        }

        foreach (var (a, b, rssi) in mesh.Links)
        {
            using var response = await http.PostAsJsonAsync("api/topology/links", new RegisterMeshLinkRequest(a, b, rssi));
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"  ! link {a}-{b}: HTTP {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
                return false;
            }
        }

        Console.WriteLine($"Registered mesh: {mesh.Nodes.Count} nodes, {mesh.Links.Count} links.");
        return true;
    }
    catch (HttpRequestException ex)
    {
        Console.WriteLine($"  ! mesh registration: gateway unreachable at {http.BaseAddress} ({ex.Message}) - is SmartXOne.Api running?");
    }
    catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
    {
        Console.WriteLine($"  ! mesh registration: gateway did not respond within {http.Timeout.TotalSeconds:0}s.");
    }
    catch (JsonException ex)
    {
        Console.WriteLine($"  ! mesh registration: request could not be serialised ({ex.Message}).");
    }

    return false;
}

/// <summary>Takes a node offline or online at the gateway; going offline also flags the device as disconnected on the Command Stream.</summary>
static async Task SetNodeOnlineAsync(HttpClient http, SimNode node, bool online)
{
    try
    {
        using var response = await http.PutAsJsonAsync(
            $"api/topology/nodes/{Uri.EscapeDataString(node.Mac)}/state", new SetMeshNodeStateRequest(online, Battery: null));
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"  ! {node.Name}: HTTP {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            return;
        }

        node.IsOnline = online;
        Console.WriteLine($"  {node.Name} ({node.Mac}) is now {(online ? "ONLINE" : "OFFLINE")}.");

        if (!online)
        {
            await SendDisconnectAsync(http, node);
        }
    }
    catch (HttpRequestException ex)
    {
        Console.WriteLine($"  ! {node.Name}: gateway unreachable at {http.BaseAddress} ({ex.Message}).");
    }
    catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
    {
        Console.WriteLine($"  ! {node.Name}: gateway did not respond within {http.Timeout.TotalSeconds:0}s.");
    }
}

static async Task SendDisconnectAsync(HttpClient http, SimNode node)
{
    try
    {
        using var response = await http.PostAsync($"api/realtime/devices/{Uri.EscapeDataString(node.Mac)}/disconnect", content: null);
        Console.WriteLine($"[disconnect] {node.Name} ({node.Mac}): HTTP {(int)response.StatusCode} {response.StatusCode}");
    }
    catch (HttpRequestException ex)
    {
        Console.WriteLine($"[disconnect] {node.Name}: gateway unreachable at {http.BaseAddress} ({ex.Message}).");
    }
    catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
    {
        Console.WriteLine($"[disconnect] {node.Name}: gateway did not respond within {http.Timeout.TotalSeconds:0}s.");
    }
}

/// <summary>
/// One reporting round: every online node sends its primary reading, plus
/// temperature (environmental sensors) and battery level as secondary
/// metrics, which the gateway files under history only. Offline nodes stay silent.
/// </summary>
/// <returns>How many readings were accepted, out of how many were sent.</returns>
static async Task<(int Accepted, int Attempted)> SendMeshRoundAsync(HttpClient http, MeshPlan mesh, bool verbose)
{
    var accepted = 0;
    var attempted = 0;
    foreach (var node in mesh.Nodes.Where(n => !n.IsGateway && n.IsOnline))
    {
        node.Drift();

        var readings = new List<(string Metric, double Value, string Unit)> { (node.PrimaryMetric, node.Value, node.Unit) };
        if (node.Kind == RealtimeDeviceKind.EnvironmentalSensor)
        {
            readings.Add((TelemetryMetrics.Temperature, node.Temperature, "C"));
        }

        readings.Add((TelemetryMetrics.Battery, node.Battery, "%"));

        foreach (var (metric, value, unit) in readings)
        {
            attempted++;
            switch (await SendReadingAsync(http, node, metric, value, unit, verbose))
            {
                case SendOutcome.Accepted:
                    accepted++;
                    break;
                case SendOutcome.Unreachable:
                    // Every other send would fail the same way (and each waits
                    // for the connection to fail), so end the round now.
                    return (accepted, attempted);
            }
        }
    }

    return (accepted, attempted);
}

/// <returns>Whether the gateway accepted the reading, rejected it, or couldn't be reached. Failures are reported to the console rather than thrown, so one bad send never ends the run.</returns>
static async Task<SendOutcome> SendReadingAsync(HttpClient http, SimNode node, string metric, double value, string unit, bool verbose)
{
    try
    {
        var request = new SubmitTelemetryRequest(node.Mac, node.Kind, Math.Round(value, 2), unit, metric);
        using var response = await http.PostAsJsonAsync("api/realtime/telemetry", request);
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"  ! {node.Name}: HTTP {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            return SendOutcome.Rejected;
        }

        if (verbose)
        {
            Console.WriteLine($"  {node.Name} {node.Mac} {metric} = {value:0.#} {unit}");
        }

        return SendOutcome.Accepted;
    }
    catch (HttpRequestException ex)
    {
        Console.WriteLine($"  ! {node.Name}: gateway unreachable at {http.BaseAddress} ({ex.Message}) - is SmartXOne.Api running?");
        return SendOutcome.Unreachable;
    }
    catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
    {
        Console.WriteLine($"  ! {node.Name}: gateway did not respond within {http.Timeout.TotalSeconds:0}s.");
        return SendOutcome.Unreachable;
    }
    catch (JsonException ex)
    {
        Console.WriteLine($"  ! {node.Name}: reading could not be serialised ({ex.Message}).");
        return SendOutcome.Rejected;
    }
}

static bool HasFlag(string[] args, string flag) => args.Contains(flag, StringComparer.OrdinalIgnoreCase);

static string? GetArgValue(string[] args, string flag)
{
    var index = Array.FindIndex(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

/// <summary>One simulated mesh node and its drifting readings.</summary>
internal sealed class SimNode(
    string mac, string name, string subnet, char? key, RealtimeDeviceKind kind, double x, double y, double value, double battery,
    bool isGateway = false)
{
    public string Mac { get; } = mac;

    public string Name { get; } = name;

    public string Subnet { get; } = subnet;

    /// <summary>Keyboard key that toggles this node; <c>null</c> for the gateway.</summary>
    public char? Key { get; } = key;

    public RealtimeDeviceKind Kind { get; } = kind;

    public bool IsGateway { get; } = isGateway;

    public double X { get; } = x;

    public double Y { get; } = y;

    /// <summary>Read by the keyboard task and the send loop, hence volatile.</summary>
    public bool IsOnline
    {
        get => Volatile.Read(ref _isOnline);
        set => Volatile.Write(ref _isOnline, value);
    }

    public double Value { get; private set; } = value;

    public double Temperature { get; private set; } = 22 + Random.Shared.NextDouble() * 4;

    public double Battery { get; private set; } = battery;

    public string PrimaryMetric => TelemetryMetrics.PrimaryFor(Kind);

    public string Unit => Kind switch
    {
        RealtimeDeviceKind.EnvironmentalSensor => "%",
        RealtimeDeviceKind.SmartPlug => "W",
        _ => "state",
    };

    private bool _isOnline = true;

    /// <summary>A small random drift from the last values - plausible-looking, not pure noise (same idea as the client's MockTelemetryGenerator).</summary>
    public void Drift()
    {
        Value = Kind switch
        {
            RealtimeDeviceKind.EnvironmentalSensor => Math.Clamp(Value + (Random.Shared.NextDouble() * 6 - 3), 5, 95),
            RealtimeDeviceKind.SmartPlug => Math.Clamp(Value + Random.Shared.Next(-100, 101), 20, 3000),
            _ => Random.Shared.NextDouble() < 0.1 ? 1 - Value : Value, // a valve occasionally flips open/shut
        };
        Temperature = Math.Clamp(Temperature + (Random.Shared.NextDouble() - 0.5), 12, 38);
        Battery = Math.Max(5, Battery - 0.02);
    }
}

/// <summary>
/// The fixed site layout: gateway at the origin, three subnets around it.
/// Each subnet is a ring with one cross-link (so losing any single node
/// leaves it connected). Six bridge links join them: one from the gateway into
/// each subnet, and one between each pair of subnets - so taking a subnet's
/// gateway-facing node offline forces its traffic to reroute through a neighbour.
/// </summary>
internal sealed class MeshPlan
{
    private MeshPlan(List<SimNode> nodes, List<(string A, string B, int Rssi)> links)
    {
        Nodes = nodes;
        Links = links;
    }

    public IReadOnlyList<SimNode> Nodes { get; }

    public IReadOnlyList<(string A, string B, int Rssi)> Links { get; }

    public static MeshPlan Build()
    {
        const string GreenhouseA = "Greenhouse A", GreenhouseB = "Greenhouse B", PumpHouse = "Pump house";
        const RealtimeDeviceKind Env = RealtimeDeviceKind.EnvironmentalSensor;
        const RealtimeDeviceKind Plug = RealtimeDeviceKind.SmartPlug;
        const RealtimeDeviceKind Valve = RealtimeDeviceKind.ActuatorNode;

        var nodes = new List<SimNode>
        {
            new("AA:BB:CC:00:00:00", "GW-00", "Core", null, Env, 0, 0, 0, 100, isGateway: true),

            new("AA:BB:CC:01:00:01", "GHA-01", GreenhouseA, '1', Env, -15, 8, 55, 96),
            new("AA:BB:CC:01:00:02", "GHA-02", GreenhouseA, '2', Env, -28, 12, 48, 88),
            new("AA:BB:CC:01:00:03", "GHA-03", GreenhouseA, '3', Env, -30, 28, 61, 72),
            new("AA:BB:CC:01:00:04", "GHA-04", GreenhouseA, '4', Plug, -14, 26, 800, 90),

            new("AA:BB:CC:02:00:01", "GHB-01", GreenhouseB, '5', Env, 15, 8, 52, 93),
            new("AA:BB:CC:02:00:02", "GHB-02", GreenhouseB, '6', Env, 28, 10, 45, 64),
            new("AA:BB:CC:02:00:03", "GHB-03", GreenhouseB, '7', Env, 32, 27, 58, 85),
            new("AA:BB:CC:02:00:04", "GHB-04", GreenhouseB, '8', Plug, 16, 28, 650, 98),

            new("AA:BB:CC:03:00:01", "PMP-01", PumpHouse, '9', Plug, -8, -15, 1200, 99),
            new("AA:BB:CC:03:00:02", "PMP-02", PumpHouse, '0', Plug, 8, -16, 950, 80),
            new("AA:BB:CC:03:00:03", "PMP-03", PumpHouse, 'a', Valve, 10, -30, 1, 70),
            new("AA:BB:CC:03:00:04", "PMP-04", PumpHouse, 'b', Valve, -10, -30, 0, 91),
        };

        var byName = nodes.ToDictionary(n => n.Name);
        var links = new List<(string, string, int)>();

        void Link(string a, string b)
        {
            var na = byName[a];
            var nb = byName[b];
            var distance = Math.Sqrt(Math.Pow(na.X - nb.X, 2) + Math.Pow(na.Y - nb.Y, 2));
            // Free-space-ish: signal weakens with distance, floored at -100 dBm.
            links.Add((na.Mac, nb.Mac, (int)Math.Max(-100, Math.Round(-40 - (distance * 1.2)))));
        }

        foreach (var prefix in new[] { "GHA", "GHB", "PMP" })
        {
            Link($"{prefix}-01", $"{prefix}-02");
            Link($"{prefix}-02", $"{prefix}-03");
            Link($"{prefix}-03", $"{prefix}-04");
            Link($"{prefix}-04", $"{prefix}-01");
            Link($"{prefix}-01", $"{prefix}-03"); // cross-link for redundancy
        }

        // Two bridges per subnet: one to the gateway, one to a neighbour.
        Link("GW-00", "GHA-01");
        Link("GW-00", "GHB-01");
        Link("GW-00", "PMP-01");
        Link("GHA-04", "GHB-04");
        Link("GHB-02", "PMP-02");
        Link("PMP-04", "GHA-02");

        return new MeshPlan(nodes, links);
    }

    /// <summary>Finds a node by MAC or by name (case-insensitive).</summary>
    public SimNode? Resolve(string? macOrName) =>
        string.IsNullOrWhiteSpace(macOrName)
            ? null
            : Nodes.FirstOrDefault(n => string.Equals(n.Mac, macOrName, StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(n.Name, macOrName, StringComparison.OrdinalIgnoreCase));

    public List<string> OfflineNames() => Nodes.Where(n => !n.IsOnline).Select(n => n.Name).ToList();
}

internal enum SendOutcome
{
    Accepted,
    Rejected,
    Unreachable,
}
