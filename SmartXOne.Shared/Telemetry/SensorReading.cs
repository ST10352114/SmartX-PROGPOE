using System.Globalization;

namespace SmartXOne.Shared.Telemetry;

/// <summary>
/// (PoE requirement 3b &ndash; OPERATOR OVERLOADING) A single numeric sensor
/// reading (e.g. a smart-meter wattage or a soil-moisture percentage) with the
/// arithmetic and comparison that the ingestion pipeline actually needs:
/// <list type="bullet">
///   <item><c>a + b</c> &mdash; aggregate two readings into a combined load
///     (e.g. total draw of two smart meters on one circuit).</item>
///   <item><c>a - b</c> &mdash; the delta between two readings (used to measure
///     how much a value jumped between consecutive samples).</item>
///   <item><c>a &gt; b</c> / <c>a &lt; b</c> (and <c>&gt;=</c> / <c>&lt;=</c>)
///     &mdash; compare readings by magnitude, e.g. "is this sample higher than
///     the previous one / the alarm threshold?".</item>
/// </list>
/// It is a <c>readonly struct</c>: a small immutable value with no heap
/// allocation, which is the natural place to hang operators.
/// </summary>
public readonly struct SensorReading
{
    public SensorReading(string sensorId, double value, string unit, DateTimeOffset? timestamp = null)
    {
        SensorId = sensorId;
        Value = value;
        Unit = unit;
        Timestamp = timestamp ?? DateTimeOffset.UtcNow;
    }

    /// <summary>Sensor id, or a composite label like <c>"meter-1 + meter-2"</c> after aggregation.</summary>
    public string SensorId { get; }

    /// <summary>The numeric reading.</summary>
    public double Value { get; }

    /// <summary>Unit of measure. <c>+</c> and <c>-</c> require both operands to share a unit.</summary>
    public string Unit { get; }

    /// <summary>When the reading was taken.</summary>
    public DateTimeOffset Timestamp { get; }

    // --- Arithmetic operators -------------------------------------------------

    /// <summary>
    /// Aggregates two readings: the values are summed. Used to report the
    /// combined load of two devices feeding the same point. Both readings must
    /// use the same unit; the result keeps the later timestamp.
    /// </summary>
    public static SensorReading operator +(SensorReading left, SensorReading right)
    {
        RequireSameUnit(left, right, "aggregate (+)");
        return new SensorReading(
            sensorId: $"{left.SensorId} + {right.SensorId}",
            value: left.Value + right.Value,
            unit: left.Unit,
            timestamp: Later(left.Timestamp, right.Timestamp));
    }

    /// <summary>
    /// The difference <c>left.Value - right.Value</c> (e.g. this sample minus the
    /// previous sample). Same-unit rule as <c>+</c>; result keeps the later timestamp.
    /// </summary>
    public static SensorReading operator -(SensorReading left, SensorReading right)
    {
        RequireSameUnit(left, right, "delta (-)");
        return new SensorReading(
            sensorId: $"{left.SensorId} - {right.SensorId}",
            value: left.Value - right.Value,
            unit: left.Unit,
            timestamp: Later(left.Timestamp, right.Timestamp));
    }

    // --- Comparison operators ------------------------------------------------
    // Compare by magnitude only. C# requires < and > to be declared as a pair,
    // and <= and >= as a pair.

    public static bool operator >(SensorReading left, SensorReading right) => left.Value > right.Value;

    public static bool operator <(SensorReading left, SensorReading right) => left.Value < right.Value;

    public static bool operator >=(SensorReading left, SensorReading right) => left.Value >= right.Value;

    public static bool operator <=(SensorReading left, SensorReading right) => left.Value <= right.Value;

    // --- Helpers -----------------------------------------------------------

    /// <summary>Absolute size of this reading, handy for "furthest from expected" checks.</summary>
    public double Magnitude => Math.Abs(Value);

    public override string ToString()
        => $"{Value.ToString("0.###", CultureInfo.InvariantCulture)} {Unit} ({SensorId})";

    private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;

    private static void RequireSameUnit(SensorReading left, SensorReading right, string operation)
    {
        if (!string.Equals(left.Unit, right.Unit, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Cannot {operation} readings with different units ('{left.Unit}' vs '{right.Unit}').");
        }
    }
}
