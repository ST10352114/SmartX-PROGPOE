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
public sealed class TelemetryQueueService
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

    internal void Enqueue(TelemetryEnvelope envelope)
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

    /// <summary>
    /// Blocks until an item is available, then returns the single
    /// highest-priority one: the priority queue first (if non-empty), the
    /// standard FIFO queue otherwise.
    /// </summary>
    internal async Task<TelemetryEnvelope> DequeueAsync(CancellationToken ct)
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

    public QueueStatusDto GetStatus() => new(StandardQueueDepth, PriorityQueueDepth, ProcessedCount);
}
