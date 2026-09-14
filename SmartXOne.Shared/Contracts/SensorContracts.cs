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

/// <summary>A registered sensor plus its most recent reading, if any has arrived yet.</summary>
public record SensorSummary(
    string SensorId,
    string DeploymentNodeId,
    SensorCategory Category,
    string? DisplayName,
    string? ProfileFileName,
    TelemetryReadingDto? LatestReading);
