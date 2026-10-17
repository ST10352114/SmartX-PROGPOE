using SmartXOne.Shared.Contracts;

namespace SmartXOne.Api.Realtime;

/// <summary>
/// Classifies an incoming reading's urgency from the actual value, the same
/// way <c>AnomalyScorer</c> does for Part 1 - the device simulator doesn't get
/// to declare "this is critical" itself, it just has to send a value extreme
/// enough to earn that classification (e.g. a real moisture-drop or power-spike
/// number), which is what its command-line flags do.
/// </summary>
internal static class TelemetrySeverity
{
    public static AlertSeverity Classify(RealtimeDeviceKind kind, double value) => kind switch
    {
        // Hydroponics moisture: a severe drop risks the crop.
        RealtimeDeviceKind.EnvironmentalSensor => value switch
        {
            < 20 => AlertSeverity.Critical,
            < 30 => AlertSeverity.Elevated,
            _ => AlertSeverity.Normal,
        },

        // Smart plug wattage: a severe spike risks the circuit/equipment.
        RealtimeDeviceKind.SmartPlug => value switch
        {
            > 2000 => AlertSeverity.Critical,
            > 1500 => AlertSeverity.Elevated,
            _ => AlertSeverity.Normal,
        },

        // Actuator state changes go through the command stream, not telemetry severity.
        RealtimeDeviceKind.ActuatorNode => AlertSeverity.Normal,

        _ => AlertSeverity.Normal,
    };
}
