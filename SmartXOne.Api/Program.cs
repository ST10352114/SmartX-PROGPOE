using SmartXOne.Api.Sensors;
using SmartXOne.Shared.Contracts;
using SmartXOne.Shared.Demo;

using System.Reflection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<SensorRepository>();

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

app.UseCors(ClientCorsPolicy);


app.MapGet("/api/gateway/info", (IWebHostEnvironment env) =>
{
    var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

    return Results.Ok(new GatewayInfo(
        Service: "Smart-X Data Ingestion and Validation Gateway",
        Version: version,
        Environment: env.EnvironmentName,
        ServerTimeUtc: DateTimeOffset.UtcNow,
        Message: "Gateway API online. Client <-> API <-> Shared round trip OK."));
});

// --- Phase 3: live proof of the four assessed C# features --------------------
// Runs the real SmartX.Shared types (generics / operator overloading / advanced
// arrays / recursion) and returns what each produced.
app.MapGet("/api/demo/oop", () => Results.Ok(SharedFeatureDemo.Run()));

// --- Phase 4: sensor registration, telemetry submission, listing, profile upload ---
app.MapSensorEndpoints();

// Also write the same report to the console on start-up.
var demo = SharedFeatureDemo.Run();
app.Logger.LogInformation("Shared feature demo:\n  Generics: {Generics}\n  Operators: {Operators}\n  Arrays: {Arrays}\n  Recursion: {Recursion}",
    demo.Generics, demo.OperatorOverloading, demo.AdvancedArrays, demo.Recursion);

app.Run();
