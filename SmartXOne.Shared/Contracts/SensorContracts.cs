// Source attribution: Microsoft .NET Class Libraries Documentation
// URL: https://learn.microsoft.com/en-us/dotnet/standard/class-libraries

using System.Text.Json;

namespace SmartXOne.Shared.Contracts;

/// <param name="SensorId">Unique id for the physical device, e.g. its MAC address.</param>
/// <param name="DeploymentNodeId">
/// Id of the <c>DeploymentNode</c> the sensor is mounted at. The API rejects
/// registration if this id isn't found anywhere in the configured deployment
/// tree (checked recursively - see <c>DeploymentTreeValidator.ContainsNode</c>).
/// </param>
/// <param name="Category">Fixes the value type readings from this sensor must use.</param>
/// <param name="DisplayName">Optional human-readable label.</param>
public record RegisterSensorRequest(
    string SensorId,
    string DeploymentNodeId,
    SensorCategory Category,
    string? DisplayName);

/// <param name="Value">
/// The raw reading. Interpreted according to the sensor's <see cref="SensorCategory"/>:
/// a JSON number for <see cref="SensorCategory.Environmental"/> (parsed as <c>float</c>)
/// or <see cref="SensorCategory.PowerConsumption"/> (parsed as <c>int</c>), or a JSON
/// boolean for <see cref="SensorCategory.Actuator"/>.
/// </param>
/// <param name="Unit">Optional override; defaults to the category's standard unit.</param>
public record SubmitReadingRequest(JsonElement Value, string? Unit);

/// <param name="Unit">Unit of measure.</param>
/// <param name="Timestamp">When the reading was taken.</param>
/// <param name="ValueType">CLR type name of the payload.</param>
/// <param name="FormattedValue">Culture-invariant string form of the payload.</param>
/// <param name="ChangeSinceLast">
/// <c>current - previous</c> (via <c>SensorReading operator -</c>), or <c>null</c>
/// if this is the sensor's first reading or its category has no numeric delta
/// (<see cref="SensorCategory.Actuator"/> state has no magnitude to subtract).
/// </param>
public record TelemetryReadingDto(
    string Unit,
    DateTimeOffset Timestamp,
    string ValueType,
    string FormattedValue,
    double? ChangeSinceLast);

/// <param name="OriginalFileName">The name the file was uploaded with.</param>
/// <param name="SizeBytes">Plaintext size in bytes.</param>
/// <param name="ContentType">The file's extension-derived content type, e.g. <c>"image/png"</c>.</param>
/// <param name="UploadedAt">When the attachment was received.</param>
/// <param name="Sha256Hex">
/// SHA-256 of the plaintext, computed while it was being encrypted
/// (<c>EncryptedAttachmentCodec.EncryptToFileAsync</c>) - lets a caller verify a
/// downloaded file's integrity end to end once the download has completed.
/// </param>
public record AttachmentMetadata(
    string OriginalFileName,
    long SizeBytes,
    string ContentType,
    DateTimeOffset UploadedAt,
    string Sha256Hex);

/// <summary>A registered sensor plus its most recent reading, if any has arrived yet.</summary>
public record SensorSummary(
    string SensorId,
    string DeploymentNodeId,
    SensorCategory Category,
    string? DisplayName,
    AttachmentMetadata? Attachment,
    TelemetryReadingDto? LatestReading);
