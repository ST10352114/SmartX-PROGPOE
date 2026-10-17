using SmartXOne.Shared.Contracts;

namespace SmartXOne.Api.Realtime;

/// <summary>One telemetry reading in flight through the queue, already classified by <see cref="TelemetrySeverity"/>.</summary>
internal sealed record TelemetryEnvelope(
    string DeviceId,
    RealtimeDeviceKind DeviceKind,
    double Value,
    string Unit,
    AlertSeverity Severity,
    DateTimeOffset Timestamp);
