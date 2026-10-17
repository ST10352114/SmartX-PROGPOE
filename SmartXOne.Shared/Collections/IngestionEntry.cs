using SmartXOne.Shared.Telemetry;

namespace SmartXOne.Shared.Collections;

/// <summary>
/// One reading as it moves through an <see cref="IngestionBuffer{T}"/>: the
/// packet itself, plus where it currently stands in the pipeline.
/// </summary>
/// <typeparam name="T">Reading value type (<c>float</c>, <c>int</c>, <c>bool</c>).</typeparam>
public sealed class IngestionEntry<T>
    where T : struct
{
    public required TelemetryPacket<T> Packet { get; init; }

    public IngestionState State { get; internal set; } = IngestionState.Received;

    /// <summary>Why <see cref="IngestionState.Rejected"/> was set; <c>null</c> otherwise.</summary>
    public string? RejectionReason { get; internal set; }
}
