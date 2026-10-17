# Smart-X IoT Mesh Ecosystem

PROG7312 (PoE)

Smart-X is a simulated IoT gateway for a distributed environment (hydroponic farms, utility
trackers, smart grid installations). It ingests telemetry from mock sensor/device nodes,
validates and stores it, and shows it live on two dashboards:

- **Part 1 — Sensor Data Ingestion and Telemetry**: register sensors, ingest validated
  multi-typed readings, attach encrypted files to a sensor's profile, and watch an anomaly
  spotlight and live KPI dashboard update automatically.
- **Part 2 — Real-Time Command Stream and History**: a queued telemetry intake pipeline
  feeding a live device registry, manual pump/relay/valve overrides with undo, a device
  simulator standing in for real hardware, and a predictive "Suggested Actions" panel.

Network Topology and Mesh Routing is visible in the navigation but disabled — it's the final
part of this PoE, not built yet.

## Architecture

| Project | Type | Purpose |
|---|---|---|
| `SmartXOne.Api` | ASP.NET Core Minimal API (.NET 10) | Both parts' endpoints: sensor ingestion, encrypted attachments, the Part 2 telemetry queue/registry/command stream, and the recommendation engine |
| `SmartXOne.Client` | Blazor WebAssembly (.NET 10) | Both dashboards, plus the app-wide toast/design-token layer they share |
| `SmartXOne.Shared` | .NET Class Library | Contracts/DTOs and the core C# requirements (`TelemetryPacket<T>`, `SensorReading`, `TelemetryBatchBuffer<T>`, `DeploymentTreeValidator`, `IngestionBuffer<T>`) used by both projects |
| `SmartXOne.DeviceSimulator` | Console app (.NET 10) | Sends real HTTP telemetry to the Part 2 endpoints, standing in for ESP32 nodes and smart plugs |

No database is used — everything is held in memory for the life of the running API process, as
specified in the brief. Restarting the API clears all data (sensors, devices, command history,
learned suggestions). Uploaded attachments are the one exception: they're written to
`SmartXOne.Api/data/` on disk (encrypted) and survive an API restart, though the in-memory
record pointing at them does not.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Visual Studio 2022 (17.10+) with the **ASP.NET and web development** workload, *or* the
  `dotnet` CLI on any OS

## Getting Started (Visual Studio)

1. Open `SmartXOne.Api.slnx` in Visual Studio.
2. Right-click the solution in Solution Explorer → **Set Startup Projects** → **Multiple
   startup projects** → set the **Action** for `SmartXOne.Api` and `SmartXOne.Client` to
   **Start** (leave `SmartXOne.DeviceSimulator` on **None** — run it separately, see below).
3. Press **F5**. Two windows/tabs should open: the API's console window, and the Client in
   your default browser.
4. If the Client shows a connection error on first load, wait a few seconds for the API to
   finish starting and refresh the page.

## Getting Started (command line / terminal)

Restore dependencies and build the whole solution:

```bash
dotnet restore
dotnet build
```

Run the API and Client in two separate terminals (both need to stay running):

```bash
# Terminal 1 — API (http://localhost:5080)
cd SmartXOne.Api
dotnet run
```

```bash
# Terminal 2 — Client (http://localhost:5081)
cd SmartXOne.Client
dotnet run
```

The Client reads the API's address from `SmartXOne.Client/wwwroot/appsettings.json`
(`ApiBaseUrl`, already set to `http://localhost:5080` to match the API's default port). If you
change the API's port in `SmartXOne.Api/Properties/launchSettings.json`, update this file to
match.

Once both are running, open `http://localhost:5081` in a browser.

### Running the device simulator (optional, for Part 2)

The Command Stream page works from manual API calls alone, but it's far more convincing with
the simulator feeding it real traffic:

```bash
# Terminal 3 — Device simulator (needs the API already running)
cd SmartXOne.DeviceSimulator
dotnet run
```

By default this continuously posts realistic telemetry for a small fleet (two environmental
sensors, two smart plugs) every 3 seconds until you press Ctrl+C. See
[Testing Each Feature](#testing-each-feature) below for its on-demand flags
(`--spike`, `--moisture-drop`, `--alert`, `--disconnect`, `--once`).

### Encryption key setup

Sensor attachments (Part 1, Fix A) are encrypted at rest with AES-256-GCM. The key comes from
configuration key `Encryption:KeyBase64` — **never hard-coded**. If it isn't set, the API falls
back to a fixed, clearly-labelled *development-only* key (with a logged warning) so a fresh
clone still runs without any setup. That fallback is fine for local marking/demo use, but set a
real key for anything beyond that:

```bash
cd SmartXOne.Api
dotnet user-secrets init
dotnet user-secrets set "Encryption:KeyBase64" "<base64-encoded 32-byte key>"
```

Generate a throwaway 32-byte key for the above, e.g.:

```bash
# PowerShell
[Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }))

# bash
openssl rand -base64 32
```

## Using the Application

### Part 1 — Sensor Data Ingestion and Telemetry

1. **Register a sensor** — fill in the MAC/unique ID, deployment location (must match a node
   in the gateway's seeded Facility → Zone → Sub-Zone → Node tree, e.g. `NODE-A3-07`), and
   category (Environmental / Power Consumption / Actuator).
2. **Generate telemetry** — "Generate one round now", or toggle auto-generate every 5s.
3. **Attach a file** — drag a file onto the upload zone (or click to browse); it's streamed,
   validated by content (not just extension), and encrypted on disk. Click its name in the
   sensor table to download/preview the decrypted file.
4. **Watch the live dashboard** — KPI row, per-sensor severity gauges and sparklines, and
   toast alerts the moment a sensor crosses into the Alert band.
5. **Report / manage issues** — the Reported Issues panel: add one, filter by severity/status,
   acknowledge or resolve it.

### Part 2 — Real-Time Command Stream and History

1. Start the [device simulator](#running-the-device-simulator-optional-for-part-2) (or submit
   telemetry manually — see testing section below) so the device grid has something to show.
2. **Search / filter** — by device id, category (moisture/power/actuator), or alert state
   (normal/warning/critical/disconnected).
3. **Issue a command** — Pump/Relay buttons on a device card, or **Shut all valves** for the
   system-wide override.
4. **Undo** — reverts the single most recent command (single-device or system-wide), restoring
   every device state it changed.
5. **View a timeline** — "Show timeline" on a device card plots its recent readings.
6. **Suggested Actions & Automated Insights** — rule-based insights (a device currently
   Critical or disconnected) and learned suggestions (what usually followed that condition
   before); a learned suggestion that resolved to a concrete command gets a one-click
   **Execute** button.

## Testing Each Feature

Each item below is a specific, repeatable check. The curl examples assume the API is running
on `http://localhost:5080`; PowerShell users can swap `curl` for `Invoke-RestMethod`.

### The FIFO queue (`Queue<T>`)

Submit a few ordinary readings and watch `GET /api/realtime/queue-status` — `standardQueueDepth`
rises as you submit and falls back to 0 almost immediately as
`TelemetryIngestionBackgroundService` drains it in arrival order:

```bash
curl -X POST http://localhost:5080/api/realtime/telemetry -H "Content-Type: application/json" \
  -d '{"DeviceId":"ESP32-ENV-01","DeviceKind":0,"Value":55,"Unit":"%"}'
curl http://localhost:5080/api/realtime/queue-status
```

### A high-priority alert jumping the queue (`PriorityQueue<TElement,TPriority>`)

From the simulator: `dotnet run -- --spike` (severe wattage, >2000W) or
`dotnet run -- --moisture-drop` (<20%) — either is classified Critical server-side and routed
to the priority queue, not the standard one. Watch `priorityQueueDepth` on
`GET /api/realtime/queue-status` tick up and immediately back down, ahead of any standard
traffic queued at the same time. Equivalently, by hand:

```bash
curl -X POST http://localhost:5080/api/realtime/telemetry -H "Content-Type: application/json" \
  -d '{"DeviceId":"ESP32-ENV-01","DeviceKind":0,"Value":12,"Unit":"%"}'
```

### `Dictionary<string,Device>` O(1) device lookup

`GET /api/realtime/devices/{id}` looks a device up directly by id (no scan) — register a
handful of devices, then fetch one specific one by id and confirm only that device comes back.

### `SortedDictionary<DateTime,_>` timeline

`GET /api/realtime/devices/{id}/timeline` — submit two or three readings for the same device a
moment apart, then fetch its timeline: the points come back already oldest-first, with no
client-side sort needed. On the Command Stream page, "Show timeline" renders this as a line
graph.

### `HashSet<T>` duplicate-disconnect suppression

```bash
curl -X POST http://localhost:5080/api/realtime/devices/ESP32-ENV-01/disconnect
# -> {"deviceId":"ESP32-ENV-01","isNewDisconnect":true}
curl -X POST http://localhost:5080/api/realtime/devices/ESP32-ENV-01/disconnect
# -> {"deviceId":"ESP32-ENV-01","isNewDisconnect":false}  - the second signal is a no-op
```

Or from the simulator: run `dotnet run -- --disconnect` twice in a row.

### Undo via the `Stack<T>`

Issue a command, then undo it, from the Command Stream page (Pump/Relay button, then "Undo
last command"), or by hand:

```bash
curl -X POST http://localhost:5080/api/realtime/devices/ESP32-ENV-01/commands \
  -H "Content-Type: application/json" -d '{"Type":0}'   # PumpOn
curl -X POST http://localhost:5080/api/realtime/commands/undo
```

The device's pump state reverts, and `GET /api/realtime/commands` shows one fewer entry.

### Triggering a Suggested Action

Repeat a condition-then-command sequence a few times so the recommendation engine has
something to learn from, then check `GET /api/recommendations/suggestions`:

```bash
for i in 1 2 3; do
  curl -X POST http://localhost:5080/api/realtime/telemetry -H "Content-Type: application/json" \
    -d '{"DeviceId":"ESP32-ENV-01","DeviceKind":0,"Value":11,"Unit":"%"}'
  curl -X POST http://localhost:5080/api/realtime/devices/PLUG-POWER-01/commands \
    -H "Content-Type: application/json" -d '{"Type":0}'
done
curl http://localhost:5080/api/recommendations/suggestions
```

A "Suggestion" entry should appear with a rising `confidence` (observed count) and an
`executableDeviceId`/`executableCommand`. On the Command Stream page it appears in the
**Suggested Actions & Automated Insights** card with an **Execute** button.

### The encrypted upload/download round trip

On the Ingestion page: register a sensor, attach a file, then click its name in the sensor
table to download it — the content you get back should be byte-identical to what you uploaded,
even though the file on disk (`SmartXOne.Api/data/*.enc`) is encrypted ciphertext, not the
original. By hand:

```bash
curl -X POST http://localhost:5080/api/sensors/<sensorId>/profile -F "file=@yourfile.png"
curl http://localhost:5080/api/sensors/<sensorId>/profile -o downloaded.png
# downloaded.png should match yourfile.png exactly
```

## Where the Required Concepts Live

### Part 1 — core C# requirements

| Requirement | Location | Notes |
|---|---|---|
| **Generics** | `SmartXOne.Shared/Telemetry/TelemetryPacket.cs` | `TelemetryPacket<T>` wraps `float`/`int`/`bool` readings without boxing; used end-to-end in `SensorRepository.SubmitReading`. |
| **Operator overloading** | `SmartXOne.Shared/Telemetry/SensorReading.cs` | `+` (combined load), `-` (delta), and `< > <= >=` (threshold comparisons). Used live in `SensorRepository.ComputeChange` and the client's `AnomalyScorer`. |
| **Advanced arrays** | `SmartXOne.Shared/Telemetry/TelemetryBatchBuffer.cs`, `SmartXOne.Api/Sensors/CategoryHistory.cs` | A jagged array (`batches[deviceIndex][readingIndex]`), then flattened to an optimised `List<T>` via `ToOptimisedList()`. |
| **Recursion** | `SmartXOne.Shared/Deployment/DeploymentTreeValidator.cs` | Walks the Facility → Zone → Sub-Zone → Node hierarchy on every sensor registration; base cases for null, a visited-set for cycles, and a depth cap. |
| **Streaming + encrypted uploads** | `SmartXOne.Api/Uploads/` | `MultipartReader`-based streaming, magic-byte content validation, chunked AES-256-GCM encryption (`EncryptedAttachmentCodec`). |
| **Custom collection** | `SmartXOne.Shared/Collections/IngestionBuffer.cs` | A generic ring buffer (`ICollection<T>`) tracking each reading's `Received → Validated → Stored/Rejected` state. |
| **Dynamic dashboard** | `SmartXOne.Client/Components/LiveDashboard.razor`, `IssuesPanel.razor` | KPIs, gauges, sparklines, toast alerts, and the reported-issues workflow. |

### Part 2 — Real-Time Command Stream and History

| Requirement | Location | Notes |
|---|---|---|
| **`Queue<T>`** | `SmartXOne.Api/Realtime/TelemetryQueueService.cs` | Standard FIFO for Normal/Elevated readings. |
| **`PriorityQueue<TElement,TPriority>`** | same file | Critical readings bypass the standard queue entirely. |
| **`BackgroundService`** | `TelemetryIngestionBackgroundService.cs` | The single consumer draining both queues. |
| **`Dictionary<string,Device>`** | `SmartXOne.Api/Realtime/DeviceRegistry.cs` | O(1) device lookup by id. |
| **`SortedDictionary<DateTime,_>`** | same file | Per-device timeline, already time-ordered. |
| **`HashSet<T>`** | same file | Deduplicates repeated disconnect signals for the same device. |
| **`Stack<T>` + Undo** | `SmartXOne.Api/Realtime/CommandHistory.cs` | LIFO command history; undo restores every device state a command changed. |
| **Device simulator** | `SmartXOne.DeviceSimulator/Program.cs` | Sends real HTTP telemetry standing in for ESP32 nodes/smart plugs. |
| **Recommendation engine** | `SmartXOne.Api/Recommendations/RecommendationEngine.cs` | `Dictionary<string, Dictionary<string,int>>` first-order Markov transition counts, plus rule checks — see the class's own doc comment for why that structure. |

## Project Structure

```
SmartXOne.Api.slnx
├── SmartXOne.Api/
│   ├── Sensors/            # SensorRepository, SensorEndpoints, CategoryHistory, DeploymentSeed
│   ├── Uploads/             # Streaming + AES-256-GCM encrypted attachments
│   ├── Issues/              # Reported-issues backend
│   ├── Realtime/            # Part 2: queue, device registry, command history, endpoints
│   ├── Recommendations/     # Predictive action / recommendation engine
│   └── Program.cs
├── SmartXOne.Client/
│   ├── Pages/               # Home.razor, Ingestion.razor, CommandStream.razor
│   ├── Components/          # AnomalySpotlightCard, LiveDashboard, IssuesPanel, ToastHost, DeviceTimelineChart
│   ├── Navigation/          # FeatureMenu.cs
│   ├── Services/            # SmartXApiClient, CommandStreamState, ToastService, AnomalyScorer
│   └── wwwroot/appsettings.json
├── SmartXOne.DeviceSimulator/
│   └── Program.cs
└── SmartXOne.Shared/
    ├── Contracts/            # SensorContracts, RealtimeContracts, IssueContracts, RecommendationContracts
    ├── Telemetry/            # TelemetryPacket, SensorReading, TelemetryBatchBuffer
    ├── Collections/          # IngestionBuffer<T>
    └── Deployment/           # DeploymentNode, DeploymentTreeValidator
```

## Known Limitations (by design)

- In-memory storage only — no database, no persistence across restarts (encrypted attachment
  *files* survive on disk; the record pointing at them does not).
- No authentication.
- Network Topology and Mesh Routing is a placeholder (final PoE part).
- The recommendation engine's event log is capped at the last 500 events and a 5-minute
  correlation window — it's a demonstrable learning loop, not a production-scale one.

## Video Demonstration

[Watch the Part 1 demo video](https://youtu.be/qF8Dhm-di1Q?si=Jetiuq1pzNJEf0Q5)

The video walks through sensor registration, live telemetry ingestion, file upload, the
anomaly spotlight, and shows where each required C# concept (generics, operator overloading,
advanced arrays/lists, recursion) runs in the actual application.

## Code Attribution

Where a class or method in this project uses an approach, API, or pattern taken from external
documentation, the comment directly above it says so (author, title, year, URL, access date).
Every one of those in-code attributions has a matching entry below, listed alphabetically by
author in Harvard (Anglia Ruskin) style.

## References

- GeeksforGeeks, n.d. *C# | Operator Overloading*. [online] Available at: <https://www.geeksforgeeks.org/c-sharp/c-sharp-operator-overloading/> [Accessed 17 October 2026].
- Microsoft, 2016. *.NET class libraries*. [online] Available at: <https://learn.microsoft.com/en-us/dotnet/standard/class-libraries> [Accessed 17 October 2026].
- Microsoft, 2023. *.NET Observability with OpenTelemetry*. [online] Available at: <https://learn.microsoft.com/en-us/dotnet/core/diagnostics/observability-with-otel> [Accessed 11 September 2026].
- Microsoft, 2025. *AesGcm Class; IncrementalHash Class (System.Security.Cryptography)*. [online] Available at: <https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm> and <https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.incrementalhash> [Accessed 17 October 2026].
- Microsoft, 2025. *Arrays (C# programming guide)*. [online] Available at: <https://learn.microsoft.com/dotnet/csharp/programming-guide/arrays/> [Accessed 9 September 2026].
- Microsoft, 2025. *ASP.NET Core Blazor WebAssembly*. [online] Available at: <https://learn.microsoft.com/aspnet/core/blazor/hosting-models#blazor-webassembly> [Accessed 11 September 2026].
- Microsoft, 2025. *Generics (C# programming guide)*. [online] Available at: <https://learn.microsoft.com/dotnet/csharp/fundamentals/types/generics> [Accessed 12 September 2026].
- Microsoft, 2025. *IFormFile and multipart file uploads in ASP.NET Core*. [online] Available at: <https://learn.microsoft.com/aspnet/core/mvc/models/file-uploads> [Accessed 11 September 2026].
- Microsoft, 2025. *Minimal APIs overview*. [online] Available at: <https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis> [Accessed 9 September 2026].
- Microsoft, 2026. *Upload files in ASP.NET Core — Upload large files with streaming*. [online] Available at: <https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads> [Accessed 17 October 2026].
- Microsoft, n.d. *ASP.NET Web APIs — Rest APIs with .NET and C#*. [online] Available at: <https://dotnet.microsoft.com/en-us/apps/aspnet/apis> [Accessed 17 October 2026].
- Telerik, n.d. *What is Blazor WebAssembly (WASM)?* [online] Available at: <https://www.telerik.com/faqs/blazor/what-is-blazor-webassembly-(wasm)> [Accessed 17 October 2026].
- Yigitbasioglu, O.M. and Velcu, O., 2012. A review of dashboards in performance management: implications for design and research. *International Journal of Accounting Information Systems*, 13(1), pp.41–59.
- Zipit Wireless, n.d. *What Are IoT Sensors? Types, Uses, and Examples*. [online] Available at: <https://www.zipitwireless.com/blog/what-are-iot-sensors-types-uses-and-examples> [Accessed 11 September 2026].
- Google. *Gemini shared conversation*. Available at: https://share.google/aimode/KmuvJF1Ei0NYTfYnU
## AI Usage Disclosure
 
In line with The IIE's Intellectual Integrity and Property Rights Policy (IIE023), AI
assistance was used during this project as follows:
 
- **Google Gemini** was used for research support and drafting assistance during
  development (see the shared session linked above under References & Attributions).

## Troubleshooting

- **"ApiBaseUrl is not configured" error in the browser** — `wwwroot/appsettings.json` in the
  Client project is missing or empty. Add the `ApiBaseUrl` key pointing at the API's port.
- **Connection refused on first load** — the API is still starting. Refresh after a few
  seconds.
- **Build errors after moving/copying the project** — confirm all four projects still
  reference `SmartXOne.Shared` where needed, and that NuGet packages have been restored
  (`dotnet restore`).
- **Encryption key warning in the API's log on start-up** — expected if
  `Encryption:KeyBase64` isn't configured; see [Encryption key setup](#encryption-key-setup).
  Harmless for local marking/demo use.
- **Command Stream page shows "Could not reach the gateway"** — the API isn't running or is
  still starting; the page keeps retrying automatically, or click **Retry now**.
