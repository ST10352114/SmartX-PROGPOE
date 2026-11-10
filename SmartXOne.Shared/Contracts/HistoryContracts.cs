namespace SmartXOne.Shared.Contracts;

/// <summary>
/// Metric names used by the telemetry history. A device's <em>primary</em>
/// metric (moisture for an environmental sensor, power for a smart plug) is
/// the one that drives the live registry and severity rules; anything else a
/// node reports alongside it (temperature, battery) is history-only.
/// </summary>
public static class TelemetryMetrics
{
    public const string Moisture = "moisture";
    public const string Power = "power";
    public const string State = "state";
    public const string Temperature = "temperature";
    public const string Battery = "battery";

    private const int MaxLength = 32;

    /// <summary>The metric a device of this kind reports when the request doesn't name one.</summary>
    public static string PrimaryFor(RealtimeDeviceKind kind) => kind switch
    {
        RealtimeDeviceKind.EnvironmentalSensor => Moisture,
        RealtimeDeviceKind.SmartPlug => Power,
        _ => State,
    };

    /// <summary>The Part 1 equivalent of <see cref="PrimaryFor(RealtimeDeviceKind)"/>, for readings submitted through the sensor API.</summary>
    public static string PrimaryFor(SensorCategory category) => category switch
    {
        SensorCategory.Environmental => Moisture,
        SensorCategory.PowerConsumption => Power,
        _ => State,
    };

    /// <summary>Lower-cases and trims <paramref name="metric"/>, falling back to the device kind's primary metric when it's blank.</summary>
    /// <exception cref="ArgumentException">The name is too long or contains anything other than letters, digits, '-' or '_'.</exception>
    public static string Normalize(string? metric, RealtimeDeviceKind kind)
    {
        if (string.IsNullOrWhiteSpace(metric))
        {
            return PrimaryFor(kind);
        }

        var trimmed = metric.Trim().ToLowerInvariant();
        if (trimmed.Length > MaxLength || !trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            throw new ArgumentException(
                $"Metric '{metric}' is invalid: use up to {MaxLength} letters, digits, '-' or '_'.", nameof(metric));
        }

        return trimmed;
    }
}

/// <summary>One stored reading from a device's history.</summary>
/// <param name="Source">Which pipeline recorded it: <c>realtime</c> (the Part 2 queue) or <c>sensor</c> (the Part 1 sensor API).</param>
public record HistoricalReadingDto(DateTimeOffset Timestamp, string Metric, double Value, string Unit, string Source);

/// <summary>Result of a history range query for one device.</summary>
/// <param name="AvailableMetrics">Every metric this device has ever reported, for the client's filter drop-down.</param>
/// <param name="TotalStored">All readings held for the device, before the range and metric filters.</param>
/// <param name="MatchedCount">Readings that matched the range and metric, before <paramref name="Readings"/> was capped to the limit.</param>
/// <param name="Readings">Matching readings, oldest first; the newest ones are kept when the match count exceeds the limit.</param>
public record TelemetryHistoryDto(
    string DeviceId,
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Metric,
    IReadOnlyList<string> AvailableMetrics,
    int TotalStored,
    int MatchedCount,
    IReadOnlyList<HistoricalReadingDto> Readings);

/// <summary>One device the history store holds readings for.</summary>
public record HistoryDeviceDto(string DeviceId, int ReadingCount, DateTimeOffset? Oldest, DateTimeOffset? Newest);
