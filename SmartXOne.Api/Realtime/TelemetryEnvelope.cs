using SmartXOne.Shared.Contracts;

namespace SmartXOne.Api.Realtime;

/// <summary>One telemetry reading in flight through the queue, already classified by <see cref="TelemetrySeverity"/>.</summary>
/// <param name="Metric">Normalised metric name - see <see cref="TelemetryMetrics"/>.</param>
/// <param name="IsPrimaryMetric">
/// <c>true</c> when <paramref name="Metric"/> is the device kind's primary metric.
/// Only primary readings update the live registry; secondary ones (temperature,
/// battery) are recorded to history only, so a device's <c>LastValue</c> on the
/// Command Stream doesn't flip between unrelated metrics.
/// </param>
internal sealed record TelemetryEnvelope(
    string DeviceId,
    RealtimeDeviceKind DeviceKind,
    double Value,
    string Unit,
    AlertSeverity Severity,
    DateTimeOffset Timestamp,
    string Metric,
    bool IsPrimaryMetric);
