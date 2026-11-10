# Feedback Updates

Muhammed Safwaan Ally (ST10352114) — PROG7312 PoE

This file lists the changes I made in response to lecturer feedback on Part 1 and Part 2. For
each point it gives the feedback, what I changed, the files affected and the commits, so every
change can be checked against `git log`. Part 1 changes were made before the Part 2 submission
(17 October 2026); Part 2 changes were made at the start of Part 3 (10 November 2026), before any
new Part 3 code was written.

## Part 1 — Sensor Data Ingestion and Telemetry

### 1. File uploads: stream them and protect them at rest

**Feedback.** The upload feature needed to be more robust: files were model-bound as `IFormFile`
and stored as-is.

**What I changed.**
- Profile uploads are now read section by section with `MultipartReader`, so a file is never
  buffered whole in memory. The size limit is enforced while streaming, and the content is
  checked by its magic bytes (PNG and JPEG signatures; .txt, .log, .json and .csv must look like text)
  rather than trusting the file extension. An oversized file is rejected with a clean 400.
- Every attachment is encrypted at rest with AES-256-GCM in fixed-size chunks, each with its own
  nonce and authentication tag, plus a SHA-256 digest of the plaintext. The key comes from
  configuration (`Encryption:KeyBase64`, e.g. user secrets) rather than code. Downloads decrypt
  and verify on the fly, so a tampered file is rejected instead of served.
- The Ingestion page shows upload progress, and an attached file can be opened
  from its link in the sensor table.

**Files.** `SmartXOne.Api/Sensors/SensorEndpoints.cs`, `SmartXOne.Api/Uploads/FileSignature.cs`,
`SmartXOne.Api/Uploads/AttachmentTooLargeException.cs`,
`SmartXOne.Api/Uploads/EncryptedAttachmentCodec.cs`,
`SmartXOne.Api/Uploads/AttachmentEncryptionKeyProvider.cs`, `SmartXOne.Api/Uploads/PrefixedStream.cs`,
`SmartXOne.Api/Sensors/SensorRecord.cs`, `SmartXOne.Api/Sensors/SensorRepository.cs`,
`SmartXOne.Api/Program.cs`, `SmartXOne.Client/Pages/Ingestion.razor`,
`SmartXOne.Client/Services/ProgressReportingStream.cs`, `SmartXOne.Client/Services/SmartXApiClient.cs`,
`SmartXOne.Shared/Contracts/SensorContracts.cs`.

**Commits.** `3deab44` Stream sensor profile uploads via MultipartReader; `cf9e632` Encrypt sensor
attachments at rest with AES-256-GCM.

### 2. A custom collection for the ingestion pipeline

**Feedback.** The ingestion code relied only on built-in collections; it needed a custom
collection that does real work in the pipeline.

**What I changed.** I added `IngestionBuffer<T>`, a generic fixed-capacity ring buffer that
implements `ICollection<T>`. Every packet entering the gateway is recorded with its state
(`Received → Validated → Stored` or `Rejected`, with the reason), and the oldest entries are
overwritten once the buffer is full. The sensor endpoints use it to report recent ingestion
activity and rejection reasons.

**Files.** `SmartXOne.Shared/Collections/IngestionBuffer.cs`, `IngestionEntry.cs`,
`IngestionState.cs`, `SmartXOne.Api/Sensors/SensorRepository.cs`, `SensorEndpoints.cs`,
`PacketValidation.cs`, `CategoryHistory.cs`, `SmartXOne.Shared/Contracts/SensorContracts.cs`.

**Commit.** `b0cf4b7` Add a custom ring-buffer collection for the ingestion pipeline.

### 3. A more engaging, live dashboard

**Feedback.** The dashboard was static and didn't engage the user.

**What I changed.** The Ingestion page now has a live dashboard: a KPI row (active devices,
packets per minute, open issues, active alerts), a severity gauge and sparkline per sensor, and
toast alerts the moment a sensor crosses into its alert band. I also added a Reported Issues
panel where an issue can be raised, filtered by severity and status, acknowledged and resolved.

**Files.** `SmartXOne.Client/Components/LiveDashboard.razor` (+ `.razor.css`),
`SmartXOne.Client/Components/IssuesPanel.razor`, `SmartXOne.Client/Services/AnomalyScorer.cs`,
`SmartXOne.Client/Pages/Ingestion.razor`, `SmartXOne.Api/Issues/IssueEndpoints.cs`,
`SmartXOne.Api/Issues/IssueRepository.cs`, `SmartXOne.Shared/Contracts/IssueContracts.cs`.

**Commit.** `e9b7526` Add a live KPI/gauge/alert dashboard and a reported-issues list.

### 4. Responsive UI and a consistent design system

**Feedback.** The interface was inconsistent and didn't give clear feedback on user actions.

**What I changed.** I added a small design-token layer (spacing, radius, type scale and one focus
ring) in `app.css` that every component uses instead of one-off values. A single app-wide toast
service gives every action the same success or error feedback, and forms now show inline
validation. Layouts use responsive Bootstrap grid breakpoints so the pages work on narrow
screens.

**Files.** `SmartXOne.Client/wwwroot/css/app.css`, `SmartXOne.Client/Services/ToastService.cs`,
`SmartXOne.Client/Components/ToastHost.razor` (+ `.razor.css`), `SmartXOne.Client/Layout/MainLayout.razor`,
`SmartXOne.Client/Pages/Ingestion.razor` (+ `.razor.css`), `SmartXOne.Client/Components/LiveDashboard.razor`,
`SmartXOne.Client/Components/IssuesPanel.razor`, `SmartXOne.Client/Program.cs`.

**Commit.** `fe4ca8c` Add a design-token layer, app-wide toasts, and crisp form feedback.

### 5. References

**Feedback.** The references needed fixing.

**What I changed.** I rewrote the reference list in Harvard (Anglia Ruskin) style, in
alphabetical order, with access dates, and added in-code attribution comments for the sources
the code actually uses. For Part 3 I merged everything into one list, added same-year
disambiguation letters (for example `Microsoft, 2026b`) that also appear in each code comment's
`Year:` line, and converted the attribution comments in every file I touched to a consistent
seven-line format (purpose, author, title, year, URL/DOI, accessed).

**Files.** `README.md`, plus the attribution comments in the files listed in its
[References](README.md#references).

**Commits.** `28d10e0` Update README: Part 2 docs, encryption key setup, per-feature test guide,
references; `1d0610a` Add implementation report and merge references.

## Part 2 — Real-Time Command Stream and History

### 6. Exception handling (try/catch) throughout

**Feedback.** Marks were deducted in every section because the code had no try/catch exception
handling.

**What I changed.**
- **Globally:** a `GlobalExceptionHandler` (`IExceptionHandler`) turns any unhandled exception
  into an RFC 7807 ProblemDetails response and logs it, and `ApiProblem` helpers give every
  endpoint the same error shape.
- **API endpoints:** every endpoint validates its input first, then wraps its work in try/catch,
  mapping expected failures to the right status code (400 bad input, 404 unknown id, 409
  conflict) and logging anything unexpected as a 500.
- **Services and data structures:** the queue service, device registry, command history,
  recommendation engine, sensor repository, upload codec and buffers catch, log with context and
  rethrow, so failures are recorded where they happen and reported where they can be handled.
  The `BackgroundService` consumer catches per packet, so one bad reading can't stop the host.
- **Client:** every API call goes through one `ApiException` type that tells "gateway offline"
  apart from "request refused". Every page, component and event handler catches and reports
  through an `ApiStatusBanner` (with Retry) or a toast, and `MainLayout` has a Blazor
  `ErrorBoundary` that recovers when the user navigates.
- **Simulator:** every HTTP call is wrapped, so an unreachable gateway is reported and the
  simulator keeps running.
- All Part 3 code was written to the same standard from the start.

**Files.** API: `SmartXOne.Api/Infrastructure/GlobalExceptionHandler.cs`, `ApiProblem.cs`,
`Program.cs`, `Issues/*`, `Realtime/*`, `Recommendations/*`, `Sensors/*`, `Uploads/*`. Shared:
`Collections/IngestionBuffer.cs`, `Deployment/DeploymentNode.cs`, `Telemetry/TelemetryBatchBuffer.cs`.
Simulator: `SmartXOne.DeviceSimulator/Program.cs`. Client: `Components/ApiStatusBanner.razor`,
`IssuesPanel.razor`, `LiveDashboard.razor`, `ToastHost.razor`, `Layout/MainLayout.razor`,
`Pages/CommandStream.razor`, `Home.razor`, `Ingestion.razor`, `Program.cs`,
`Services/CommandStreamState.cs`, `SmartXApiClient.cs`, `ToastService.cs`.

**Commits.** `3ad4598` Add try/catch and ProblemDetails error handling across the API; `a300fab`
Handle API failures and offline state across the client.

### 7. Remove the "coming soon" sidebar label

**Feedback.** The sidebar still showed a disabled "Coming in…" entry for an unbuilt feature.

**What I changed.** The navigation now lists only working pages, from one `FeatureMenu` source
shared by the sidebar and the Home cards, so the two can't drift apart. Network Topology and
Routing was added back as a real, working page once it was built. I also moved the Ingestion
page's live state into a scoped `IngestionState` service, so auto-generation and the latest data
survive navigating away and back, in the same way the Command Stream page already worked.

**Files.** `SmartXOne.Client/Navigation/FeatureMenu.cs`, `Layout/NavMenu.razor`,
`Layout/MainLayout.razor`, `Pages/Home.razor`, `Pages/Ingestion.razor`,
`Services/IngestionState.cs`, `Program.cs`.

**Commits.** `03ce081` Remove coming-soon nav labels and keep ingestion state across navigation;
`2a6d536` Add topology page with mesh map, route search and reachability.
