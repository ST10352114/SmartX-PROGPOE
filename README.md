# Smart-X IoT Mesh Ecosystem

PROG7312 (PoE) — Muhammed Safwaan Ally (ST10352114)

Smart-X is a simulated IoT gateway for a distributed environment (hydroponic farms, utility
trackers, smart grid installations). It ingests telemetry from mock sensor/device nodes,
validates and stores it, routes it across a self-healing mesh, and shows it live on three
dashboards:

- **Part 1 — Sensor Data Ingestion and Telemetry**: register sensors, ingest validated
  multi-typed readings, attach encrypted files to a sensor's profile, and watch an anomaly
  spotlight and live KPI dashboard update automatically.
- **Part 2 — Real-Time Command Stream and History**: a queued telemetry intake pipeline
  feeding a live device registry, manual pump/relay/valve overrides with undo, a device
  simulator standing in for real hardware, and a predictive "Suggested Actions" panel.
- **Part 3 — Network Topology and Mesh Routing**: the mesh as a weighted graph, a minimum
  spanning tree (Prim's algorithm on a custom min-heap) that traffic follows to the gateway,
  BFS reachability and DFS island detection, rerouting when a node goes offline, and a
  per-sensor history kept in a self-balancing AVL tree for fast time-range queries.

## Table of Contents

- [Architecture](#architecture)
- [Prerequisites](#prerequisites)
- [Getting Started (Visual Studio)](#getting-started-visual-studio)
- [Getting Started (command line / terminal)](#getting-started-command-line--terminal)
  — [Running the device simulator](#running-the-device-simulator)
  — [Running the unit tests](#running-the-unit-tests)
  — [Encryption key setup](#encryption-key-setup)
- [Using the Application](#using-the-application)
  — [Part 1 — Sensor Data Ingestion and Telemetry](#part-1--sensor-data-ingestion-and-telemetry)
  — [Part 2 — Real-Time Command Stream and History](#part-2--real-time-command-stream-and-history)
  — [Part 3 — Network Topology and Routing](#part-3--network-topology-and-routing)
- [Testing Each Feature](#testing-each-feature)
- [Where the Required Concepts Live](#where-the-required-concepts-live)
- [Project Structure](#project-structure)
- [Known Limitations (by design)](#known-limitations-by-design)
- [Video Demonstration](#video-demonstration)
- [Code Attribution](#code-attribution)
- [References](#references)
- [AI Usage Disclosure](#ai-usage-disclosure)
- [Troubleshooting](#troubleshooting)

## Architecture

| Project | Type | Purpose |
|---|---|---|
| `SmartXOne.Api` | ASP.NET Core Minimal API (.NET 10) | All three parts' endpoints: sensor ingestion, encrypted attachments, the Part 2 telemetry queue/registry/command stream and recommendation engine, and the Part 3 topology and history endpoints. Global exception handling returns RFC 7807 ProblemDetails. |
| `SmartXOne.Client` | Blazor WebAssembly (.NET 10) | The three dashboards, plus the toast/design-token/error-banner layer they share. Each page's live state sits in a scoped service, so it survives navigating away and back. |
| `SmartXOne.Shared` | .NET Class Library | Contracts/DTOs and the core data structures used by both sides: `TelemetryPacket<T>`, `SensorReading`, `TelemetryBatchBuffer<T>`, `DeploymentTreeValidator`, `IngestionBuffer<T>`, and for Part 3 `AvlTree<TKey,TValue>`, `MinHeap<T>`, `MeshGraph` and `MeshAlgorithms` (BFS, DFS, Prim). |
| `SmartXOne.DeviceSimulator` | Console app (.NET 10) | Registers a 13-node mesh with the gateway and sends real HTTP telemetry for it, standing in for ESP32 nodes, smart plugs and valve actuators. Nodes can be knocked offline from the keyboard. |
| `SmartXOne.Tests` | xUnit test project (.NET 10) | Unit tests for the AVL tree, the min-heap and the mesh algorithms (Prim is cross-checked against Kruskal on random meshes). |

No database is used — everything is held in memory for the life of the running API process, as
specified in the brief. Restarting the API clears all data (sensors, devices, command history,
learned suggestions, the mesh and its history). Uploaded attachments are the one exception:
they're written to `SmartXOne.Api/data/` on disk (encrypted) and survive an API restart, though
the in-memory record pointing at them does not.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) — check with `dotnet --version`
- Visual Studio 2026 (or any version that supports .NET 10 and `.slnx` solutions) with the
  **ASP.NET and web development** workload, *or* the `dotnet` CLI on any OS
- A modern browser (Chrome, Edge or Firefox) for the Blazor WebAssembly client

## Getting Started (Visual Studio)

1. Open `SmartXOne.Api.slnx` in Visual Studio.
2. Right-click the solution in Solution Explorer → **Configure Startup Projects** → **Multiple
   startup projects** → set the **Action** for `SmartXOne.Api` and `SmartXOne.Client` to
   **Start** (leave `SmartXOne.DeviceSimulator` and `SmartXOne.Tests` on **None** — run the
   simulator separately, see below).
3. Press **F5**. Two windows/tabs should open: the API's console window, and the Client in
   your default browser.
4. If the Client shows a "Gateway API offline" banner on first load, wait a few seconds for
   the API to finish starting — every page retries automatically.
5. To run the tests: **Test** → **Run All Tests** (Test Explorer).

## Getting Started (command line / terminal)

Restore dependencies and build the whole solution from the repository root:

```bash
dotnet restore SmartXOne.Api.slnx
dotnet build SmartXOne.Api.slnx
```

Run the API and Client in two separate terminals (both need to stay running):

```bash
# Terminal 1 — API (http://localhost:5080)
dotnet run --project SmartXOne.Api --launch-profile http
```

```bash
# Terminal 2 — Client (http://localhost:5081)
dotnet run --project SmartXOne.Client --launch-profile http
```

The Client reads the API's address from `SmartXOne.Client/wwwroot/appsettings.json`
(`ApiBaseUrl`, already set to `http://localhost:5080` to match the API's default port). If you
change the API's port in `SmartXOne.Api/Properties/launchSettings.json`, update this file to
match.

Once both are running, open `http://localhost:5081` in a browser.

### Running the device simulator

The Command Stream and Topology pages work from manual API calls alone, but they come alive
with the simulator feeding them real traffic. Start it once the API is running:

```bash
# Terminal 3 — Device simulator (needs the API already running)
dotnet run --project SmartXOne.DeviceSimulator
```

On start-up it registers a 13-node mesh with the gateway — `GW-00` plus three subnets of four
nodes each (Greenhouse A `GHA-01..04`, Greenhouse B `GHB-01..04`, Pump house `PMP-01..04`)
joined by 21 radio links — then every 3 seconds sends each node's primary reading (moisture,
power or valve state) plus its temperature and battery level. Every device is identified by its
MAC address, e.g. `AA:BB:CC:01:00:01` for `GHA-01`.

While it runs, the console prints a key map: press a node's key (`1`-`9`, `0`, `a`, `b`) to
toggle that node offline/online — the Topology page reroutes around it within one poll — and
`q` to quit.

One-shot flags, each of which sends its signal and exits:

| Command | Effect |
|---|---|
| `dotnet run --project SmartXOne.DeviceSimulator -- --once` | Register the mesh, send one round of telemetry, exit |
| `... -- --spike` | One severe power spike (> 2000 W) — Critical, jumps the priority queue |
| `... -- --moisture-drop` | One moisture reading under 20 % — Critical |
| `... -- --alert` | One generic high-priority alert |
| `... -- --disconnect` | A disconnect signal for one node (`--disconnect-device <mac or name>` to pick) |
| `... -- --node-offline <mac or name>` | Take a mesh node offline, e.g. `--node-offline PMP-01` |
| `... -- --node-online <mac or name>` | Bring a mesh node back online |
| `... -- --api http://host:port` | Point the simulator at a different gateway URL |

### Running the unit tests

```bash
dotnet test SmartXOne.Api.slnx
```

This runs the 30 xUnit tests in `SmartXOne.Tests`: AVL insertion in all four rotation cases,
duplicate keys, range queries and balance checks after thousands of random inserts; min-heap
ordering; and BFS, DFS, path reconstruction and Prim's minimum spanning forest (checked against
Kruskal's algorithm on random meshes, including disconnected ones).

### Encryption key setup

Sensor attachments (Part 1) are encrypted at rest with AES-256-GCM. The key comes from
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

The sidebar lists the three modules; the Home page has a card for each. If the API stops, every
page shows a red "Gateway API offline" banner with a **Retry now** button and keeps showing the
last data it received, and polling pages recover by themselves once the API is back.

### Part 1 — Sensor Data Ingestion and Telemetry

1. **Register a sensor** — fill in the MAC/unique ID, deployment location (must match a node
   in the gateway's seeded Facility → Zone → Sub-Zone → Node tree, e.g. `NODE-A3-07`), and
   category (Environmental / Power Consumption / Actuator).
2. **Generate telemetry** — "Generate one round now", or toggle auto-generate every 5s. The
   toggle stays on when you move to another page and come back.
3. **Attach a file** — drag a file onto the upload zone (or click to browse); it's streamed,
   validated by content (not just extension), and encrypted on disk. Click its name in the
   sensor table to download/preview the decrypted file.
4. **Watch the live dashboard** — KPI row, per-sensor severity gauges and sparklines, and
   toast alerts the moment a sensor crosses into the Alert band.
5. **Report / manage issues** — the Reported Issues panel: add one, filter by severity/status,
   acknowledge or resolve it.

### Part 2 — Real-Time Command Stream and History

1. Start the [device simulator](#running-the-device-simulator) (or submit telemetry manually -
   see the testing section below) so the device grid has something to show.
2. **Search / filter** — by device id (MAC), category (moisture/power/actuator), or alert state
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

### Part 3 — Network Topology and Routing

1. Start the [device simulator](#running-the-device-simulator) so the mesh is registered, then
   open **Network Topology and Routing**.
2. **Read the mesh map** — every node is drawn at its site-plan position. The purple square is
   the gateway; green nodes are online and routed to it, amber ones are online but cut off, grey
   ones are offline. Thick blue links form the minimum spanning tree, thin grey links are spare
   radio links, red dashed links touch an offline node. The legend under the map repeats this.
3. **Find a route** — type a node's name or MAC (suggestions appear as you type) and press
   **Find route**. The path from the gateway is drawn in orange on the map with numbered hops,
   and the panel lists the hops, the path cost, and the fewest-hop (BFS) path for comparison.
4. **Knock a node offline** — click it on the map (or Tab to it and press Enter), or use
   **Take offline** in the nodes table. Links touching it turn red, the spanning tree is
   rebuilt, and a searched route that went through it is redrawn around it with a
   **Rerouted** badge and the previous path. Click it again to bring it back. The gateway
   can't be taken offline.
5. **Reachability sweep** — pick the gateway, a subnet or a single node in **Sweep from**: a
   breadth-first search lists every node reachable from there with its hop count, and every
   node that isn't (offline, or online but cut off).
6. **Spanning tree and components** — the MST card lists the tree's links and total cost; the
   components card shows each island of connected nodes, flagging any that can't reach the
   gateway.
7. **Sensor history** — in **Sensor history**, pick a device (or press **History** on a row of
   the nodes table), optionally set **From**/**To** or a quick range and a metric, and press
   **Query**. The readings come back from the AVL range query as a chart with time and value
   axes and a table you can sort by time, metric or value. Part 1 sensor readings appear here
   too, marked with a `sensor` source badge.

## Testing Each Feature

Each item below is a specific, repeatable check. The curl examples assume the API is running
on `http://localhost:5080`; PowerShell users can swap `curl` for `Invoke-RestMethod`. Device ids
are MAC addresses: `AA:BB:CC:01:00:01` is `GHA-01` (an environmental sensor) and
`AA:BB:CC:01:00:04` is `GHA-04` (a smart plug).

### The FIFO queue (`Queue<T>`)

Submit a few ordinary readings and watch `GET /api/realtime/queue-status` — `standardQueueDepth`
rises as you submit and falls back to 0 almost immediately as
`TelemetryIngestionBackgroundService` drains it in arrival order:

```bash
curl -X POST http://localhost:5080/api/realtime/telemetry -H "Content-Type: application/json" \
  -d '{"DeviceId":"AA:BB:CC:01:00:01","DeviceKind":0,"Value":55,"Unit":"%"}'
curl http://localhost:5080/api/realtime/queue-status
```

### A high-priority alert jumping the queue (`PriorityQueue<TElement,TPriority>`)

From the simulator: `dotnet run --project SmartXOne.DeviceSimulator -- --spike` (severe
wattage, > 2000 W) or `-- --moisture-drop` (< 20 %) — either is classified Critical server-side
and routed to the priority queue, not the standard one. Watch `priorityQueueDepth` on
`GET /api/realtime/queue-status` tick up and immediately back down, ahead of any standard
traffic queued at the same time. Equivalently, by hand:

```bash
curl -X POST http://localhost:5080/api/realtime/telemetry -H "Content-Type: application/json" \
  -d '{"DeviceId":"AA:BB:CC:01:00:01","DeviceKind":0,"Value":12,"Unit":"%"}'
```

### `Dictionary<string,Device>` O(1) device lookup

`GET /api/realtime/devices/{id}` looks a device up directly by id (no scan) — with the
simulator running, fetch one device and confirm only that device comes back:

```bash
curl http://localhost:5080/api/realtime/devices/AA:BB:CC:01:00:01
```

### `SortedDictionary<DateTime,_>` timeline

`GET /api/realtime/devices/{id}/timeline` — submit two or three readings for the same device a
moment apart, then fetch its timeline: the points come back already oldest-first, with no
client-side sort needed. On the Command Stream page, "Show timeline" renders this as a line
graph.

### `HashSet<T>` duplicate-disconnect suppression

```bash
curl -X POST http://localhost:5080/api/realtime/devices/AA:BB:CC:01:00:01/disconnect
# -> {"deviceId":"AA:BB:CC:01:00:01","isNewDisconnect":true}
curl -X POST http://localhost:5080/api/realtime/devices/AA:BB:CC:01:00:01/disconnect
# -> {"deviceId":"AA:BB:CC:01:00:01","isNewDisconnect":false}  - the second signal is a no-op
```

Or from the simulator: run `dotnet run --project SmartXOne.DeviceSimulator -- --disconnect`
twice in a row.

### Undo via the `Stack<T>`

Issue a command, then undo it, from the Command Stream page (Pump/Relay button, then "Undo
last command"), or by hand:

```bash
curl -X POST http://localhost:5080/api/realtime/devices/AA:BB:CC:01:00:01/commands \
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
    -d '{"DeviceId":"AA:BB:CC:01:00:01","DeviceKind":0,"Value":11,"Unit":"%"}'
  curl -X POST http://localhost:5080/api/realtime/devices/AA:BB:CC:01:00:04/commands \
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

### Error handling (try/catch and ProblemDetails)

Every failure comes back as an RFC 7807 ProblemDetails body with the right status code rather
than a stack trace or an empty 500:

```bash
curl -i http://localhost:5080/api/topology/route/AA:BB:CC:99:99:99      # 404 - unknown node
curl -i -X PUT http://localhost:5080/api/topology/nodes/AA:BB:CC:00:00:00/state \
  -H "Content-Type: application/json" -d '{"online":false}'            # 409 - the gateway can't go offline
curl -i "http://localhost:5080/api/history/AA:BB:CC:01:00:01?from=2030-01-01T00:00:00Z&to=2029-01-01T00:00:00Z"  # 400 - from after to
```

Then stop the API while the Client is open: each page shows the red "Gateway API offline"
banner instead of crashing, and recovers on its own when the API is restarted.

### The minimum spanning tree and routing (Prim, `MinHeap<T>`)

With the simulator running (or after `-- --once`):

```bash
curl http://localhost:5080/api/topology                         # nodes, links, link costs, version
curl http://localhost:5080/api/topology/mst                     # tree edges and total cost
curl http://localhost:5080/api/topology/route/AA:BB:CC:03:00:04 # PMP-04: GW-00 -> PMP-01 -> PMP-04
```

### Rerouting around a failed node

Take the route's middle hop offline, then ask for the same route again — it now goes the long
way round through Greenhouse B, with `"rerouted": true`, the old path in `previousPath` and
`PMP-01` in `avoidedNodes`:

```bash
curl -X PUT http://localhost:5080/api/topology/nodes/AA:BB:CC:03:00:01/state \
  -H "Content-Type: application/json" -d '{"online":false}'
curl http://localhost:5080/api/topology/route/AA:BB:CC:03:00:04
curl -X PUT http://localhost:5080/api/topology/nodes/AA:BB:CC:03:00:01/state \
  -H "Content-Type: application/json" -d '{"online":true}'      # put it back
```

On the Topology page the same thing happens by searching `PMP-04` and clicking `PMP-01` on the
map, or by pressing `9` in the simulator's console.

### BFS reachability and DFS components

```bash
curl http://localhost:5080/api/topology/reachable                 # from the gateway
curl "http://localhost:5080/api/topology/reachable?from=Pump%20house"  # from a whole subnet
curl http://localhost:5080/api/topology/components                # islands of connected nodes
```

Take `PMP-01`, `PMP-02` and `GHA-02` offline and `PMP-03`/`PMP-04` become an island of their
own: `components` returns a second entry with `"containsGateway": false`, and both nodes move
to `unreachable` in the gateway's reachability sweep.

### AVL tree history range query

```bash
curl http://localhost:5080/api/history                  # devices with history, reading counts, oldest/newest
curl "http://localhost:5080/api/history/AA:BB:CC:01:00:01?metric=temperature"
curl "http://localhost:5080/api/history/AA:BB:CC:01:00:01?from=2026-11-10T10:00:00Z&to=2026-11-10T10:15:00Z"
```

`matchedCount` against `totalStored` shows the range and metric filters at work; readings come
back oldest first straight from the tree's in-order range walk.

### Unit tests

```bash
dotnet test SmartXOne.Api.slnx   # 30 tests, all passing
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
| **`BackgroundService`** | `TelemetryIngestionBackgroundService.cs` | The single consumer draining both queues; catches per packet so one bad reading can't stop the host. |
| **`Dictionary<string,Device>`** | `SmartXOne.Api/Realtime/DeviceRegistry.cs` | O(1) device lookup by id. |
| **`SortedDictionary<DateTime,_>`** | same file | Per-device timeline, already time-ordered. |
| **`HashSet<T>`** | same file | Deduplicates repeated disconnect signals for the same device. |
| **`Stack<T>` + Undo** | `SmartXOne.Api/Realtime/CommandHistory.cs` | LIFO command history; undo restores every device state a command changed. |
| **Device simulator** | `SmartXOne.DeviceSimulator/Program.cs` | Sends real HTTP telemetry standing in for ESP32 nodes/smart plugs. |
| **Recommendation engine** | `SmartXOne.Api/Recommendations/RecommendationEngine.cs` | `Dictionary<string, Dictionary<string,int>>` first-order Markov transition counts, plus rule checks — see the class's own doc comment for why that structure. |
| **Error handling** | `SmartXOne.Api/Infrastructure/`, every endpoint and page | `IExceptionHandler` + ProblemDetails globally, try/catch with logging in every endpoint, service, page and component handler, and a Blazor `ErrorBoundary` in `MainLayout`. |

### Part 3 — Network Topology and Mesh Routing

| Requirement | Location | Notes |
|---|---|---|
| **AVL tree** | `SmartXOne.Shared/Collections/AvlTree.cs` | Generic self-balancing BST keyed by `DateTimeOffset`; LL/RR/LR/RL rotations, duplicate timestamps kept in arrival order, pruned `Range(from, to)`. |
| **Historical querying** | `SmartXOne.Api/History/TelemetryHistoryStore.cs`, `HistoryEndpoints.cs` | One AVL tree per device, fed by both the Part 1 sensor API and the Part 2 queue; `GET /api/history/{deviceId}?from&to&metric&limit`. |
| **Graph** | `SmartXOne.Shared/Topology/MeshGraph.cs` | Undirected weighted adjacency list; link cost from distance, RSSI and battery. |
| **BFS / DFS** | `SmartXOne.Shared/Topology/MeshAlgorithms.cs` | `Queue<T>` BFS for reachability and fewest-hop paths; iterative `Stack<T>` DFS for connected components. |
| **Min-heap** | `SmartXOne.Shared/Collections/MinHeap.cs` | Array-backed binary heap with a pluggable `IComparer<T>`; Prim's frontier. |
| **Prim's MST** | `MeshAlgorithms.MinimumSpanningForest` | Lazy Prim from the gateway, then from each leftover pocket, so isolated islands get their own tree. |
| **Routing + self-healing** | `SmartXOne.Api/Topology/TopologyService.cs`, `TopologyEndpoints.cs` | Recomputes the tree on every change, tracks each route's previous path and the offline nodes it now avoids. |
| **Topology dashboard** | `SmartXOne.Client/Pages/Topology.razor`, `Components/MeshMap.razor`, `SensorHistoryPanel.razor`, `HistoryChart.razor` | SVG mesh map, route search, reachability, components, MST summary and the sensor history panel. |
| **Unit tests** | `SmartXOne.Tests/` | `AvlTreeTests`, `MinHeapTests`, `MeshAlgorithmsTests`. |

## Project Structure

```
SmartXOne.Api.slnx
├── SmartXOne.Api/
│   ├── Infrastructure/      # GlobalExceptionHandler, ApiProblem (ProblemDetails helpers)
│   ├── Sensors/             # SensorRepository, SensorEndpoints, CategoryHistory, DeploymentSeed
│   ├── Uploads/             # Streaming + AES-256-GCM encrypted attachments
│   ├── Issues/              # Reported-issues backend
│   ├── Realtime/            # Part 2: queue, device registry, command history, endpoints
│   ├── Recommendations/     # Predictive action / recommendation engine
│   ├── History/             # Part 3: AVL-backed telemetry history store and endpoints
│   ├── Topology/            # Part 3: TopologyService (MST, routes, reroutes) and endpoints
│   └── Program.cs
├── SmartXOne.Client/
│   ├── Pages/               # Home, Ingestion, CommandStream, Topology
│   ├── Components/          # LiveDashboard, IssuesPanel, ToastHost, ApiStatusBanner, MeshMap,
│   │                        # SensorHistoryPanel, HistoryChart, DeviceTimelineChart, ...
│   ├── Navigation/          # FeatureMenu.cs
│   ├── Services/            # SmartXApiClient, IngestionState, CommandStreamState, TopologyState, ToastService
│   └── wwwroot/appsettings.json
├── SmartXOne.DeviceSimulator/
│   └── Program.cs           # 13-node mesh, telemetry rounds, keyboard node toggles
├── SmartXOne.Shared/
│   ├── Contracts/           # Sensor, Realtime, Issue, Recommendation, History, Topology contracts
│   ├── Telemetry/           # TelemetryPacket, SensorReading, TelemetryBatchBuffer
│   ├── Collections/         # IngestionBuffer<T>, AvlTree<TKey,TValue>, MinHeap<T>
│   ├── Topology/            # MeshGraph, MeshAlgorithms (BFS, DFS, Prim)
│   └── Deployment/          # DeploymentNode, DeploymentTreeValidator
└── SmartXOne.Tests/         # xUnit: AvlTreeTests, MinHeapTests, MeshAlgorithmsTests
```

## Known Limitations (by design)

- In-memory storage only — no database, no persistence across restarts (encrypted attachment
  *files* survive on disk; the record pointing at them does not).
- No authentication.
- The dashboards poll the API every few seconds rather than receiving pushed updates.
- Routes follow the minimum spanning tree, not the cheapest individual path — the tree
  minimises the mesh's total link cost, so one node's route can be longer than its shortest
  path. The route panel shows the fewest-hop path alongside for comparison.
- Telemetry history keeps everything for the life of the process; there is no retention limit
  or down-sampling.
- The recommendation engine's event log is capped at the last 500 events and a 5-minute
  correlation window — it's a demonstrable learning loop, not a production-scale one.

## Video Demonstration

[Watch the Part 1 demo video](https://youtu.be/qF8Dhm-di1Q?si=Jetiuq1pzNJEf0Q5)

Part 2 Demo : https://youtu.be/HFOcycsGsBg?si=0iHvWOxfodBg6PIL

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
- **Pages show a red "Gateway API offline" banner** — the API isn't running or is still
  starting; the pages keep retrying automatically, or click **Retry now**.
- **Topology page says no mesh nodes yet** — the mesh is registered by the device simulator;
  start it (or run it once with `-- --once`). Restarting the API clears the mesh, so run the
  simulator again afterwards.
- **Sensor history says "No history is stored" for a device** — the device hasn't reported a
  reading since the API started. The gateway (`GW-00`) routes traffic but never reports
  readings of its own.
- **Simulator keys do nothing** — the keyboard toggles need an interactive console; they're
  disabled when input is redirected. Use `--node-offline` / `--node-online` instead.
