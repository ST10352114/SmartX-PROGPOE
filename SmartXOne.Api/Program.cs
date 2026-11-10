// Code Attribution
// Purpose: Hosting model of an ASP.NET Core API serving a separately-hosted
//          Blazor WebAssembly client.
// Author: Progress Telerik
// Title: What is Blazor WebAssembly (WASM)?
// Year: 2025
// URL/DOI: https://www.telerik.com/faqs/blazor/what-is-blazor-webassembly-(wasm)
// Accessed: 10 November 2026

using SmartXOne.Api.Infrastructure;
using SmartXOne.Api.Issues;
using SmartXOne.Api.Realtime;
using SmartXOne.Api.Recommendations;
using SmartXOne.Api.Sensors;
using SmartXOne.Api.Uploads;
using SmartXOne.Shared.Contracts;
using SmartXOne.Shared.Demo;

using System.Reflection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<SensorRepository>();
builder.Services.AddSingleton<AttachmentEncryptionKeyProvider>();
builder.Services.AddSingleton<IssueRepository>();

// Part 2 - Real-Time Command Stream: queue/registry/history are singletons so
// both API request threads and the background consumer share the same state;
// the background service is the queue's single reader.
builder.Services.AddSingleton<TelemetryQueueService>();
builder.Services.AddSingleton<DeviceRegistry>();
builder.Services.AddSingleton<CommandHistory>();
builder.Services.AddHostedService<TelemetryIngestionBackgroundService>();

// Part 2 - Predictive Action & Recommendation Engine: reads live state from
// DeviceRegistry, so it's registered after it.
builder.Services.AddSingleton<RecommendationEngine>();

// Global safety net behind every endpoint's own try/catch: anything that still
// escapes is logged and returned as an RFC 7807 ProblemDetails body.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// Explicit outer ceiling on any request body (Kestrel's own default is ~28.6 MB).
// Attachments are additionally capped per-file inside the upload endpoint itself
// (SensorEndpoints.MaxAttachmentSizeBytes); this is just the server-wide backstop.
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 25 * 1024 * 1024);

// CORS: the Blazor WASM client is served from a different origin (port) during
// development, so the browser needs the API to explicitly allow that origin.
const string ClientCorsPolicy = "SmartXClient";
builder.Services.AddCors(options =>
{
    options.AddPolicy(ClientCorsPolicy, policy =>
    {
        var allowedOrigins = builder.Configuration
            .GetSection("Cors:AllowedOrigins")
            .Get<string[]>() ?? [];

        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseCors(ClientCorsPolicy);


app.MapGet("/api/gateway/info", (IWebHostEnvironment env, ILogger<Program> logger) =>
{
    try
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

        return Results.Ok(new GatewayInfo(
            Service: "Smart-X Data Ingestion and Validation Gateway",
            Version: version,
            Environment: env.EnvironmentName,
            ServerTimeUtc: DateTimeOffset.UtcNow,
            Message: "Gateway API online. Client <-> API <-> Shared round trip OK."));
    }
    catch (Exception ex)
    {
        return ApiProblem.Unexpected(logger, ex, "Read gateway info", subject: null);
    }
});

// --- Phase 3: live proof of the four assessed C# features --------------------
// Runs the real SmartX.Shared types (generics / operator overloading / advanced
// arrays / recursion) and returns what each produced.
app.MapGet("/api/demo/oop", (ILogger<Program> logger) =>
{
    try
    {
        return Results.Ok(SharedFeatureDemo.Run());
    }
    catch (ArgumentException ex)
    {
        return ApiProblem.Handled(logger, ex, StatusCodes.Status400BadRequest, "Run shared feature demo", subject: null);
    }
    catch (Exception ex)
    {
        return ApiProblem.Unexpected(logger, ex, "Run shared feature demo", subject: null);
    }
});

// --- Phase 4: sensor registration, telemetry submission, listing, profile upload ---
app.MapSensorEndpoints();

// --- Fix C: user-reported issues backing the dashboard's engagement feature ---
app.MapIssueEndpoints();

// --- Part 2: telemetry queue/priority-queue intake, device registry, command stream ---
app.MapRealtimeEndpoints();

// --- Part 2: predictive action / recommendation engine ---
app.MapRecommendationEndpoints();


// The start-up demo is informational only, so a failure in it is logged and
// the gateway still starts instead of taking every endpoint down with it.
try
{
    var demo = SharedFeatureDemo.Run();
    app.Logger.LogInformation("Shared feature demo:\n  Generics: {Generics}\n  Operators: {Operators}\n  Arrays: {Arrays}\n  Recursion: {Recursion}",
        demo.Generics, demo.OperatorOverloading, demo.AdvancedArrays, demo.Recursion);
}
catch (Exception ex)
{
    app.Logger.LogError(ex, "Shared feature demo failed at start-up; continuing without it.");
}

app.Run();
