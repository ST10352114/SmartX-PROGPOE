using SmartXOne.Shared.Collections;
using SmartXOne.Shared.Contracts;

namespace SmartXOne.Api.History;

/// <summary>
/// Per-device telemetry history, one <see cref="AvlTree{TKey,TValue}"/> per
/// device keyed by UTC timestamp. Both ingestion pipelines feed it: every
/// envelope the Part 2 queue consumer processes, and every reading the Part 1
/// sensor API stores. The balanced tree is what lets
/// <see cref="Query"/> answer "readings between 10:00 and 10:15" in
/// O(log n + k) instead of scanning a device's whole history.
/// <para>
/// A single lock guards the dictionary and every tree: writes come from the
/// one background consumer plus occasional sensor API calls, so contention is low.
/// </para>
/// </summary>
public sealed class TelemetryHistoryStore(ILogger<TelemetryHistoryStore> logger)
{
    public const string RealtimeSource = "realtime";
    public const string SensorSource = "sensor";

    /// <summary>Default and maximum number of readings one query returns.</summary>
    public const int DefaultLimit = 500;
    public const int MaxLimit = 5000;

    private readonly Dictionary<string, DeviceHistory> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <exception cref="ArgumentException"><paramref name="deviceId"/>, <paramref name="metric"/> or <paramref name="source"/> is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is NaN or infinite.</exception>
    public void Record(string deviceId, string metric, double value, string unit, DateTimeOffset timestamp, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(metric);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "History values must be finite numbers.");
        }

        try
        {
            var reading = new HistoricalReadingDto(timestamp.ToUniversalTime(), metric, value, unit ?? "", source);
            lock (_gate)
            {
                if (!_devices.TryGetValue(deviceId, out var history))
                {
                    history = new DeviceHistory();
                    _devices[deviceId] = history;
                }

                history.Tree.Add(reading.Timestamp.UtcDateTime, reading);
                history.Metrics.Add(metric);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to record {Metric} history for {DeviceId}.", metric, deviceId);
            throw;
        }
    }

    /// <summary>
    /// Readings for <paramref name="deviceId"/> between <paramref name="from"/>
    /// and <paramref name="to"/> (inclusive; either may be omitted), optionally
    /// only one metric, oldest first. When more than <paramref name="limit"/>
    /// match, the newest <paramref name="limit"/> are returned.
    /// </summary>
    /// <returns><c>null</c> if the store has never seen this device.</returns>
    /// <exception cref="ArgumentException"><paramref name="deviceId"/> is blank, or <paramref name="from"/> is after <paramref name="to"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is outside 1..<see cref="MaxLimit"/>.</exception>
    public TelemetryHistoryDto? Query(string deviceId, DateTimeOffset? from, DateTimeOffset? to, string? metric, int limit = DefaultLimit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxLimit);
        if (from is { } f && to is { } t && f > t)
        {
            throw new ArgumentException("'from' must not be after 'to'.", nameof(from));
        }

        var metricFilter = string.IsNullOrWhiteSpace(metric) ? null : metric.Trim().ToLowerInvariant();

        try
        {
            lock (_gate)
            {
                if (!_devices.TryGetValue(deviceId, out var history))
                {
                    return null;
                }

                var lower = from?.UtcDateTime ?? DateTime.MinValue;
                var upper = to?.UtcDateTime ?? DateTime.MaxValue;

                var matched = history.Tree.Range(lower, upper)
                    .Select(entry => entry.Value)
                    .Where(r => metricFilter is null || r.Metric == metricFilter)
                    .ToList();

                var readings = matched.Count > limit ? matched.GetRange(matched.Count - limit, limit) : matched;

                return new TelemetryHistoryDto(
                    deviceId,
                    from,
                    to,
                    metricFilter,
                    history.Metrics.Order().ToList(),
                    history.Tree.Count,
                    matched.Count,
                    readings);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "History query failed for {DeviceId} ({From} to {To}, metric {Metric}).",
                deviceId, from, to, metricFilter);
            throw;
        }
    }

    /// <summary>Every device with stored history, with its reading count and time span.</summary>
    public IReadOnlyList<HistoryDeviceDto> GetDevices()
    {
        try
        {
            lock (_gate)
            {
                return _devices
                    .Select(pair =>
                    {
                        // Leftmost / rightmost keys bound the span in O(log n) each.
                        var tree = pair.Value.Tree;
                        DateTimeOffset? oldest = tree.TryGetMinKey(out var min) ? new DateTimeOffset(min, TimeSpan.Zero) : null;
                        DateTimeOffset? newest = tree.TryGetMaxKey(out var max) ? new DateTimeOffset(max, TimeSpan.Zero) : null;
                        return new HistoryDeviceDto(pair.Key, tree.Count, oldest, newest);
                    })
                    .OrderBy(d => d.DeviceId, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to list devices with history.");
            throw;
        }
    }

    private sealed class DeviceHistory
    {
        public AvlTree<DateTime, HistoricalReadingDto> Tree { get; } = new();

        public HashSet<string> Metrics { get; } = new(StringComparer.Ordinal);
    }
}
