using System.Globalization;
using SmartXOne.Shared.Contracts;

namespace SmartXOne.Client.Services;

/// <summary>
/// No physical sensors are wired up yet, so the ingestion page uses this to
/// produce plausible-looking readings per <see cref="SensorCategory"/>: a
/// small random step from the sensor's last reading (falling back to a
/// sensible baseline for the first reading) rather than pure noise, so the
/// live table doesn't look obviously random.
/// </summary>
public static class MockTelemetryGenerator
{
    public static object NextValue(SensorCategory category, TelemetryReadingDto? previous) => category switch
    {
        SensorCategory.Environmental => NextEnvironmental(previous),
        SensorCategory.PowerConsumption => NextPowerConsumption(previous),
        SensorCategory.Actuator => Random.Shared.NextDouble() < 0.3, // valve mostly closed
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, message: null),
    };

    private static float NextEnvironmental(TelemetryReadingDto? previous)
    {
        var baseline = TryParsePrevious(previous, fallback: 55f);
        var next = baseline + (float)((Random.Shared.NextDouble() * 6) - 3); // +/-3% drift
        return Math.Clamp(next, 5f, 95f);
    }

    private static int NextPowerConsumption(TelemetryReadingDto? previous)
    {
        var baseline = (int)TryParsePrevious(previous, fallback: 800f);
        var next = baseline + Random.Shared.Next(-150, 151);
        return Math.Clamp(next, 20, 3000);
    }

    private static float TryParsePrevious(TelemetryReadingDto? previous, float fallback)
        => previous is not null && float.TryParse(previous.FormattedValue, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
}
