using System.Globalization;
using SmartXOne.Shared.Contracts;
using SmartXOne.Shared.Telemetry;

namespace SmartXOne.Client.Services;

public enum AnomalySeverity
{
    Nominal,
    Watch,
    Alert,
}

/// <summary>One sensor's deviation from its category's expected operating band, worst-first.</summary>
public sealed record AnomalyAssessment(SensorSummary Sensor, double Score, AnomalySeverity Severity, string Explanation);

/// <summary>
/// Scores how far each sensor's latest reading sits outside its category's
/// expected band, so the dashboard can spotlight whichever registered sensor
/// is currently the biggest outlier. Pure function over data already on
/// screen (Phase 5's seeded readings) - no extra API calls, so it's cheap to
/// recompute on every render and stays live as new readings arrive.
/// </summary>
public static class AnomalyScorer
{
    private static readonly (float Min, float Max) EnvironmentalBand = (20f, 80f); // % moisture
    private static readonly (float Min, float Max) PowerBand = (200f, 1200f);      // W

    /// <returns>The sensor with the highest deviation score, or <c>null</c> if none has a reading yet.</returns>
    public static AnomalyAssessment? FindSpotlight(IEnumerable<SensorSummary> sensors)
    {
        AnomalyAssessment? worst = null;
        foreach (var sensor in sensors)
        {
            if (Assess(sensor) is not { } assessment)
            {
                continue;
            }

            if (worst is null || assessment.Score > worst.Score)
            {
                worst = assessment;
            }
        }

        return worst;
    }

    private static AnomalyAssessment? Assess(SensorSummary sensor)
    {
        if (sensor.LatestReading is not { } reading)
        {
            return null;
        }

        return sensor.Category switch
        {
            SensorCategory.Environmental => AssessBand(sensor, reading, EnvironmentalBand, "%"),
            SensorCategory.PowerConsumption => AssessBand(sensor, reading, PowerBand, "W"),
            SensorCategory.Actuator => AssessActuator(sensor, reading),
            _ => null,
        };
    }

    private static AnomalyAssessment AssessBand(
        SensorSummary sensor, TelemetryReadingDto reading, (float Min, float Max) band, string unit)
    {
        var value = float.TryParse(reading.FormattedValue, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : (band.Min + band.Max) / 2;
        var bandWidth = band.Max - band.Min;

        // (PoE requirement 3b - OPERATOR OVERLOADING, applied) SensorReading's <
        // and > are exactly "is this sample past a threshold?" (its own doc
        // comment's example use case) - used here instead of comparing the raw
        // floats directly.
        var currentReading = new SensorReading(sensor.SensorId, value, unit, reading.Timestamp);
        var minThreshold = new SensorReading("expected-min", band.Min, unit, reading.Timestamp);
        var maxThreshold = new SensorReading("expected-max", band.Max, unit, reading.Timestamp);

        double score;
        string explanation;
        if (currentReading < minThreshold)
        {
            score = Math.Min(1.0, (band.Min - value) / bandWidth);
            explanation = $"{value:0.#}{unit} is {band.Min - value:0.#}{unit} below the expected minimum of {band.Min:0.#}{unit}.";
        }
        else if (currentReading > maxThreshold)
        {
            score = Math.Min(1.0, (value - band.Max) / bandWidth);
            explanation = $"{value:0.#}{unit} is {value - band.Max:0.#}{unit} above the expected maximum of {band.Max:0.#}{unit}.";
        }
        else
        {
            score = 0;
            explanation = $"{value:0.#}{unit} is within the expected {band.Min:0.#}-{band.Max:0.#}{unit} range.";
        }

        return new AnomalyAssessment(sensor, score, ToSeverity(score), explanation);
    }

    /// <summary>An actuator's expected resting state is closed/idle; being open counts as fully outside that.</summary>
    private static AnomalyAssessment AssessActuator(SensorSummary sensor, TelemetryReadingDto reading)
    {
        var isOpen = string.Equals(reading.FormattedValue, "True", StringComparison.OrdinalIgnoreCase);
        var score = isOpen ? 1.0 : 0.0;
        var explanation = isOpen
            ? "Actuator is open/active; expected resting state is closed/idle."
            : "Actuator is closed/idle, as expected.";
        return new AnomalyAssessment(sensor, score, ToSeverity(score), explanation);
    }

    private static AnomalySeverity ToSeverity(double score) => score switch
    {
        <= 0 => AnomalySeverity.Nominal,
        < 0.5 => AnomalySeverity.Watch,
        _ => AnomalySeverity.Alert,
    };
}
