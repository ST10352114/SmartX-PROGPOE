using SmartXOne.Shared.Contracts;

namespace SmartXOne.Api.Realtime;

/// <summary>
/// (Part 2 requirement - QUEUE / PRIORITY QUEUE) The front door for incoming
/// telemetry: <see cref="Enqueue"/> is called from API request threads (one
/// call per POST), <see cref="DequeueAsync"/> is called from the single
/// <see cref="TelemetryIngestionBackgroundService"/> consumer loop.
/// <para>
/// A plain <see cref="Queue{T}"/> holds Normal/Elevated readings, processed
/// strictly first-in-first-out. A <see cref="PriorityQueue{TElement,TPriority}"/>
/// holds Critical readings and is always drained first, so a severe power
/// spike or a moisture reading under 20% jumps straight to the front of
/// processing instead of waiting behind whatever normal traffic arrived first.
/// </para>
/// <para>
/// Neither collection is thread-safe on its own, so every access goes through
/// <see cref="_gate"/> - the brief for this project asks for exactly that
/// (locks around the plain collections), not a swap to a <c>Concurrent*</c>
/// type. A <see cref="SemaphoreSlim"/> signals the single consumer so it can
/// await new work instead of polling.
/// </para>
/// </summary>
public sealed class TelemetryQueueService(ILogger<TelemetryQueueService> logger)
{
    private readonly Queue<TelemetryEnvelope> _standard = new();
    private readonly PriorityQueue<TelemetryEnvelope, int> _priority = new();
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _itemAvailable = new(0);

    private long _processedCount;

    public int StandardQueueDepth
    {
        get { lock (_gate) { return _standard.Count; } }
    }

    public int PriorityQueueDepth
    {
        get { lock (_gate) { return _priority.Count; } }
    }

    public long ProcessedCount => Interlocked.Read(ref _processedCount);

    /// <exception cref="ArgumentNullException"><paramref name="envelope"/> is null.</exception>
    internal void Enqueue(TelemetryEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        try
        {
            lock (_gate)
            {
                if (envelope.Severity == AlertSeverity.Critical)
                {
                    _priority.Enqueue(envelope, (int)envelope.Severity);
                }
                else
                {
                    _standard.Enqueue(envelope);
                }
            }

            _itemAvailable.Release();
        }
        catch (SemaphoreFullException ex)
        {
            // Only possible if the semaphore count ever exceeded int.MaxValue -
            // the item is queued but the consumer won't be woken for it.
            logger.LogError(ex, "Queue signal overflowed while enqueuing telemetry from {DeviceId}.", envelope.DeviceId);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to enqueue {Severity} telemetry from {DeviceId}.", envelope.Severity, envelope.DeviceId);
            throw;
        }
    }

    /// <summary>
    /// Blocks until an item is available, then returns the single
    /// highest-priority one: the priority queue first (if non-empty), the
    /// standard FIFO queue otherwise.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled while waiting.</exception>
    internal async Task<TelemetryEnvelope> DequeueAsync(CancellationToken ct)
    {
        try
        {
            await _itemAvailable.WaitAsync(ct);

            lock (_gate)
            {
                var envelope = _priority.TryDequeue(out var priorityItem, out _)
                    ? priorityItem
                    : _standard.Dequeue();

                Interlocked.Increment(ref _processedCount);
                return envelope;
            }
        }
        catch (OperationCanceledException)
        {
            throw; // normal shutdown signal - the caller decides what to do with it
        }
        catch (InvalidOperationException ex)
        {
            // The semaphore said an item was ready but both queues were empty -
            // the signal and the collections have drifted out of step.
            logger.LogError(ex, "Queue signal and queue contents are out of step (signalled, but both queues empty).");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected failure dequeuing telemetry.");
            throw;
        }
    }

    public QueueStatusDto GetStatus()
    {
        try
        {
            return new(StandardQueueDepth, PriorityQueueDepth, ProcessedCount);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read queue status.");
            throw;
        }
    }
}
