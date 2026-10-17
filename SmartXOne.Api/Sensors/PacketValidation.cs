using SmartXOne.Shared.Telemetry;

namespace SmartXOne.Api.Sensors;

/// <summary>
/// Data-quality rules run by each category's <c>IngestionBuffer&lt;T&gt;</c> on
/// every reading, on top of whatever type/shape validation already happened
/// while parsing the request (see <see cref="SensorRepository.SubmitReading"/>).
/// A reading can be the right CLR type and still be nonsense data - this is
/// where that gets caught and tagged <c>Rejected</c> instead of silently stored.
/// </summary>
internal static class PacketValidation
{
    /// <summary>Environmental readings are percentages (soil moisture, humidity) - 0-100 or it's not real.</summary>
    public static string? Environmental(TelemetryPacket<float> packet)
        => packet.Value is < 0f or > 100f
            ? $"{packet.Value}% is outside the valid 0-100% range for an environmental reading."
            : null;

    /// <summary>A smart meter can't report negative wattage.</summary>
    public static string? Power(TelemetryPacket<int> packet)
        => packet.Value < 0
            ? $"{packet.Value} W is negative, which isn't a valid power reading."
            : null;

    /// <summary>Every <c>bool</c> value (on/off) is a valid actuator state - nothing to reject.</summary>
    public static string? Actuator(TelemetryPacket<bool> packet) => null;
}
