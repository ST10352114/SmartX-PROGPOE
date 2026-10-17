using SmartXOne.Shared.Contracts;
using SmartXOne.Shared.Telemetry;

namespace SmartXOne.Api.Sensors;

/// <summary>
/// Server-side record for one registered sensor. Kept separate from
/// <see cref="SensorSummary"/>: this type holds the live <see cref="ITelemetryPacket"/>
/// (still generic-backed, no boxing) and is mutated in place as readings and
/// profile uploads arrive; <see cref="SensorSummary"/> is the flattened,
/// JSON-friendly snapshot handed back to callers.
/// </summary>
public sealed class SensorRecord(string sensorId, string deploymentNodeId, SensorCategory category, string? displayName)
{
    public string SensorId { get; } = sensorId;

    public string DeploymentNodeId { get; } = deploymentNodeId;

    public SensorCategory Category { get; } = category;

    public string? DisplayName { get; } = displayName;

    /// <summary>
    /// Name of the encrypted file on disk under the API's <c>data/</c> folder.
    /// Internal only - never serialised back to a client, which only ever sees
    /// <see cref="Attachment"/> and fetches content via the profile download
    /// endpoint rather than a raw file path.
    /// </summary>
    public string? AttachmentStoredFileName { get; set; }

    /// <summary>Client-facing metadata for the attached file, if one has been uploaded.</summary>
    public AttachmentMetadata? Attachment { get; set; }

    /// <summary>Most recent reading, or <c>null</c> until the first one is submitted.</summary>
    public ITelemetryPacket? LatestReading { get; set; }

    /// <summary>
    /// <c>current - previous</c> for the two most recent readings, computed via
    /// <c>SensorReading operator -</c> in <see cref="SensorRepository.SubmitReading"/>.
    /// <c>null</c> before a second reading arrives, or for <see cref="SensorCategory.Actuator"/>
    /// (a bool state has no numeric delta).
    /// </summary>
    public double? ChangeSinceLast { get; set; }

    public SensorSummary ToSummary() => new(
        SensorId: SensorId,
        DeploymentNodeId: DeploymentNodeId,
        Category: Category,
        DisplayName: DisplayName,
        Attachment: Attachment,
        LatestReading: LatestReading is null
            ? null
            : new TelemetryReadingDto(
                Unit: LatestReading.Unit,
                Timestamp: LatestReading.Timestamp,
                ValueType: LatestReading.ValueType,
                FormattedValue: LatestReading.FormatValue(),
                ChangeSinceLast: ChangeSinceLast));
}
