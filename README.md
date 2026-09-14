# Smart-X IoT Mesh Ecosystem — Part 1: Data Ingestion and Validation Gateway

PROG7312 (PoE, Part 1)

Smart-X is a simulated IoT gateway that ingests telemetry from mock sensor nodes (soil
moisture, power wattage, valve state), validates and stores it, and shows it live on a
dashboard with automatic anomaly detection. This part covers the ingestion pipeline only —
Real-Time Command Stream (Part 2) and Network Topology/Mesh Routing (Part 3) are visible in
the navigation but disabled.

## Architecture

| Project | Type | Purpose |
|---|---|---|
| `SmartXOne.Api` | ASP.NET Core Minimal API (.NET 10) | Receives, validates, and stores telemetry and sensor registrations |
| `SmartXOne.Client` | Blazor WebAssembly (.NET 10) | Dashboard UI — registration, live telemetry, file upload, anomaly spotlight |
| `SmartXOne.Shared` | .NET Class Library | Shared models used by both projects (`TelemetryPacket<T>`, contracts, validation) |

No database is used for Part 1 — sensors and readings are held in memory for the life of the
running API process, as specified in the brief. Restarting the API clears all data.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Visual Studio 2022 (17.10+) with the **ASP.NET and web development** workload, *or* the
  `dotnet` CLI on any OS
- (Optional) Docker Desktop, if running via containers

## Getting Started (Visual Studio)

1. Open `SmartXOne.sln` in Visual Studio.
2. Right-click the solution in Solution Explorer → **Set Startup Projects** → **Multiple
   startup projects** → set the **Action** for both `SmartXOne.Api` and `SmartXOne.Client`
   to **Start**.
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
# Terminal 1 — API
cd SmartXOne.Api
dotnet run
```

```bash
# Terminal 2 — Client
cd SmartXOne.Client
dotnet run
```

Note the port the API starts on (shown in the terminal output, or check
`SmartXOne.Api/Properties/launchSettings.json`). The Client reads this from
`SmartXOne.Client/wwwroot/appsettings.json` — confirm the `ApiBaseUrl` value there matches
the API's actual port before running.

```json
{
  "ApiBaseUrl": "https://localhost:XXXX"
}
```

Once both are running, open the Client's URL (shown in its terminal output) in a browser.

## Running with Docker

```bash
docker compose up --build
```

This builds and starts both the API and Client containers together. See `docker-compose.yml`
for the exposed ports.

## Using the Application

1. **Register a sensor** — on the "Sensor Data Ingestion and Telemetry" page, fill in the
   MAC/unique ID, deployment location (Facility → Zone → Sub-Zone → Node), and category
   (Environmental / Power Consumption / Actuator), then submit.
2. **Generate telemetry** — use the "Generate one round now" button, or toggle
   auto-simulate, to post mock readings for registered sensors.
3. **Attach a file** — use the upload control next to a sensor to attach a config file, log,
   or photo to that sensor's profile.
4. **Watch the anomaly spotlight** — the dashboard highlights whichever sensor's latest
   reading is furthest outside its expected range, updating live as new readings arrive.
5. **View history** — expand a sensor's row to see its buffered historical readings.

## Where the Required C# Concepts Live

| Requirement | Location | Notes |
|---|---|---|
| **Generics** | `SmartXOne.Shared/Telemetry/TelemetryPacket.cs` | `TelemetryPacket<T>` wraps float, int, and bool readings without boxing; used end-to-end in `SensorRepository.SubmitReading`. |
| **Operator overloading** | `SmartXOne.Shared/Telemetry/SensorReading.cs` | `+` (combined load), `-` (change since last reading), and comparison operators. `-` and comparisons are used live in `SensorRepository.ComputeChangeSinceLast` and `AnomalyScorer.AssessBand`. |
| **Advanced arrays/lists** | `SmartXOne.Shared/Telemetry/TelemetryBatchBuffer.cs`, `CategoryHistory.cs` | Historical readings are buffered in a jagged array per sensor, then exposed as a `List<T>` via `ToOptimisedList()`. |
| **Recursion** | `SmartXOne.Shared/Validation/DeploymentTreeValidator.cs` | Recursively validates a sensor's deployment node against the Facility → Zone → Sub-Zone → Node hierarchy on every registration; has a cycle guard and max-depth base case. |

## Project Structure

```
SmartXOne.sln
├── SmartXOne.Api/
│   ├── Sensors/            # SensorRepository, SensorEndpoints
│   └── Program.cs
├── SmartXOne.Client/
│   ├── Pages/              # Home.razor, Ingestion.razor
│   ├── Components/         # AnomalySpotlightCard.razor
│   ├── Navigation/         # FeatureMenu.cs
│   ├── Services/           # SmartXApiClient.cs
│   └── wwwroot/appsettings.json
└── SmartXOne.Shared/
    ├── Contracts/          # SensorContracts.cs
    ├── Telemetry/          # TelemetryPacket.cs, SensorReading.cs, TelemetryBatchBuffer.cs
    └── Validation/         # DeploymentTreeValidator.cs
```

## Known Limitations (by design, for Part 1)

- In-memory storage only — no database, no persistence across restarts.
- No authentication.
- Real-Time Command Stream and Network Topology pages are placeholders (Parts 2 and 3).

## Video Demonstration

[Watch the Part 1 demo video](https://youtu.be/qF8Dhm-di1Q?si=Jetiuq1pzNJEf0Q5)

The video walks through sensor registration, live telemetry ingestion, file upload, the
anomaly spotlight, and shows where each required C# concept (generics, operator overloading,
advanced arrays/lists, recursion) runs in the actual application.

## References

- Microsoft (2025) *Minimal APIs overview*. Available at: https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis (Accessed: 9 September 2026).
- Microsoft (2025) *ASP.NET Core Blazor WebAssembly*. Available at: https://learn.microsoft.com/aspnet/core/blazor/hosting-models#blazor-webassembly (Accessed: 11 September 2026).
- Microsoft (2025) *Generics (C# programming guide)*. Available at: https://learn.microsoft.com/dotnet/csharp/fundamentals/types/generics (Accessed: 12 September 2026).
- GeeksforGeeks. *C# | Operator Overloading*. Available at: https://www.geeksforgeeks.org/c-sharp/c-sharp-operator-overloading/
- Microsoft (2025) *Arrays (C# programming guide)*. Available at: https://learn.microsoft.com/dotnet/csharp/programming-guide/arrays/ (Accessed: 9 September 2026).
- Microsoft. *Observability with OpenTelemetry in .NET*. Available at: https://learn.microsoft.com/en-us/dotnet/core/diagnostics/observability-with-otel (Accessed: 11 September 2026).
- Microsoft (2025) *IFormFile and multipart file uploads in ASP.NET Core*. Available at: https://learn.microsoft.com/aspnet/core/mvc/models/file-uploads (Accessed: 11 September 2026).
- Zipit Wireless. *What Are IoT Sensors? Types, Uses, and Examples*. Available at: https://www.zipitwireless.com/blog/what-are-iot-sensors-types-uses-and-examples (Accessed: 11 September 2026).
- Yigitbasioglu, O.M. and Velcu, O. (2012) 'A review of dashboards in performance management: implications for design and research', *International Journal of Accounting Information Systems*, 13(1), pp. 41–59.
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
- **Build errors after moving/copying the project** — confirm all three projects still
  reference `SmartXOne.Shared`, and that NuGet packages have been restored (`dotnet restore`).
