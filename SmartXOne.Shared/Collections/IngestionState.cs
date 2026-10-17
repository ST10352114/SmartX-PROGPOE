namespace SmartXOne.Shared.Collections;

/// <summary>
/// Where one reading currently sits in the ingestion pipeline.
/// <see cref="Received"/> and <see cref="Validated"/> are transient - they
/// describe what <see cref="IngestionBuffer{T}.Ingest"/> is doing to a reading
/// on its way through, not something held onto afterwards. Only the two
/// terminal states are kept: <see cref="Stored"/> (held in the buffer) or
/// <see cref="Rejected"/> (held in the buffer's short rejected log, with a reason).
/// </summary>
public enum IngestionState
{
    Received,
    Validated,
    Stored,
    Rejected,
}
