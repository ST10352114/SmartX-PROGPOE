namespace SmartXOne.Shared.Contracts;

/// <summary>
/// The three sensor kinds the gateway accepts. Each category fixes the
/// <c>TelemetryPacket&lt;T&gt;</c> value type and default unit the API uses when
/// it builds a reading for that sensor: <see cref="Environmental"/> readings are
/// <c>float</c> percentages (soil moisture, humidity), <see cref="PowerConsumption"/>
/// readings are <c>int</c> watts, and <see cref="Actuator"/> readings are
/// <c>bool</c> on/off state.
/// </summary>
public enum SensorCategory
{
    Environmental = 0,
    PowerConsumption = 1,
    Actuator = 2,
}
