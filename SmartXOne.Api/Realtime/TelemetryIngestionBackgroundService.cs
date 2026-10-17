using SmartXOne.Shared.Contracts;

namespace SmartXOne.Api.Realtime;

/// <summary>
/// The single consumer draining <see cref="TelemetryQueueService"/>: one
/// always-running loop, started by the host, that awaits the next envelope
/// (priority queue first, standard queue otherwise - see
/// <see cref="TelemetryQueueService.DequeueAsync"/>) and applies it to the
/// <see cref="DeviceRegistry"/>. Being the only writer touching the registry
/// from the queue side is what makes a single <see cref="Lock"/> per
/// collection enough for thread safety here, instead of needing
/// <c>Concurrent*</c> collections.
/// </summary>
public sealed class TelemetryIngestionBackgroundService(
    TelemetryQueueService queue,
    DeviceRegistry registry,
    ILogger<TelemetryIngestionBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Telemetry ingestion background service started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            TelemetryEnvelope envelope;
            try
            {
                envelope = await queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // host is shutting down
            }

            registry.ApplyTelemetry(envelope);

            if (envelope.Severity == AlertSeverity.Critical)
            {
                logger.LogWarning(
                    "CRITICAL telemetry from {DeviceId}: {Value}{Unit} (processed ahead of the standard queue)",
                    envelope.DeviceId, envelope.Value, envelope.Unit);
            }
        }

        logger.LogInformation("Telemetry ingestion background service stopped.");
    }
}
