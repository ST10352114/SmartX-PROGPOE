using SmartXOne.Api.Recommendations;
using SmartXOne.Shared.Contracts;

namespace SmartXOne.Api.Realtime;

// Code Attribution
// Purpose: Since .NET 6 an exception escaping BackgroundService.ExecuteAsync
//          stops the whole host by default, so the consumer loop catches per
//          packet instead of letting one bad reading take the gateway down.
// Author: Microsoft
// Title: .NET 6 breaking change: Exception handling in hosting
// Year: 2022
// URL/DOI: https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/6.0/hosting-exception-handling
// Accessed: 10 November 2026

/// <summary>
/// The single consumer draining <see cref="TelemetryQueueService"/>: one
/// always-running loop, started by the host, that awaits the next envelope
/// (priority queue first, standard queue otherwise - see
/// <see cref="TelemetryQueueService.DequeueAsync"/>) and applies it to the
/// <see cref="DeviceRegistry"/>. Being the only writer touching the registry
/// from the queue side is what makes a single <see cref="Lock"/> per
/// collection enough for thread safety here, instead of needing
/// <c>Concurrent*</c> collections.
/// <para>
/// Each packet is processed inside its own try/catch: a malformed or
/// unexpected reading is logged with its device id and skipped, and the loop
/// carries on with the next one. Only host shutdown ends the loop.
/// </para>
/// </summary>
public sealed class TelemetryIngestionBackgroundService(
    TelemetryQueueService queue,
    DeviceRegistry registry,
    RecommendationEngine recommendations,
    ILogger<TelemetryIngestionBackgroundService> logger) : BackgroundService
{
    /// <summary>Pause after an unexpected dequeue failure so a persistent fault can't spin the CPU.</summary>
    private static readonly TimeSpan FaultBackoff = TimeSpan.FromSeconds(1);

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
            catch (Exception ex)
            {
                logger.LogError(ex, "Dequeue failed; retrying after {Backoff}.", FaultBackoff);
                try
                {
                    await Task.Delay(FaultBackoff, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            ProcessEnvelope(envelope);
        }

        logger.LogInformation("Telemetry ingestion background service stopped.");
    }

    private void ProcessEnvelope(TelemetryEnvelope envelope)
    {
        try
        {
            var becameCritical = registry.ApplyTelemetry(envelope);

            if (envelope.Severity == AlertSeverity.Critical)
            {
                logger.LogWarning(
                    "CRITICAL telemetry from {DeviceId}: {Value}{Unit} (processed ahead of the standard queue)",
                    envelope.DeviceId, envelope.Value, envelope.Unit);
            }

            // Only the transition INTO Critical is logged as a "condition" event
            // for the recommendation engine - not every subsequent Critical
            // reading, or one ongoing spike would dominate the event log.
            if (becameCritical)
            {
                recommendations.LogConditionCritical(envelope.DeviceId);
            }
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Skipped a malformed telemetry packet from {DeviceId}.", envelope.DeviceId);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Registry rejected telemetry from {DeviceId}; packet skipped.", envelope.DeviceId);
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: one bad packet must not stop the host.
            logger.LogError(ex, "Unexpected failure processing telemetry from {DeviceId}; packet skipped.", envelope.DeviceId);
        }
    }
}
