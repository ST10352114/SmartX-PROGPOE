namespace SmartXOne.Shared.Telemetry;

/// <summary>
/// Non-generic view over a telemetry packet. Storage and UI code can hold a
/// collection of these and read the metadata / a formatted value string without
/// ever needing to know the concrete payload type <c>T</c>.
/// <para>
/// Important: this interface deliberately does NOT expose the payload as
/// <see cref="object"/>. Handing out <c>object Value</c> would box a
/// <see cref="float"/>/<see cref="int"/>/<see cref="bool"/> payload. Instead the
/// only value accessor is <see cref="FormatValue"/>, which produces a
/// <see cref="string"/> for display &mdash; formatting is not boxing.
/// </para>
/// </summary>
public interface ITelemetryPacket
{
    /// <summary>Id of the sensor that produced the reading (MAC address or unique id).</summary>
    string SensorId { get; }

    /// <summary>Unit of measure, e.g. <c>"%"</c>, <c>"W"</c>, <c>"state"</c>.</summary>
    string Unit { get; }

    /// <summary>When the reading was taken (UTC).</summary>
    DateTimeOffset Timestamp { get; }

    /// <summary>CLR type name of the payload: <c>"Single"</c>, <c>"Int32"</c> or <c>"Boolean"</c>.</summary>
    string ValueType { get; }

    /// <summary>Culture-invariant string form of the payload, for tables/logs.</summary>
    string FormatValue();
}

/// <summary>
/// (PoE requirement 3a &ndash; GENERICS) Reusable, strongly-typed wrapper around a
/// single sensor reading of type <typeparamref name="T"/> plus its metadata.
/// <para>
/// The type parameter is constrained to <c>struct</c>, so the payload is always a
/// value type (<see cref="float"/> for moisture/temperature, <see cref="int"/> for
/// power wattage, <see cref="bool"/> for valve/switch state). <typeparamref name="T"/>
/// is stored in a <c>T</c> field and read back as <c>T</c> &mdash; it is never cast
/// to <see cref="object"/>, put in a non-generic collection, or pattern-matched
/// against a type, so no boxing or unboxing happens anywhere in the pipeline.
/// </para>
/// </summary>
/// <typeparam name="T">The reading's value type: <c>float</c>, <c>int</c> or <c>bool</c>.</typeparam>
public sealed class TelemetryPacket<T> : ITelemetryPacket
    where T : struct
{
    /// <inheritdoc />
    public required string SensorId { get; init; }

    /// <summary>The reading itself, kept as <typeparamref name="T"/> end to end.</summary>
    public required T Value { get; init; }

    /// <inheritdoc />
    public required string Unit { get; init; }

    /// <inheritdoc />
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    /// <remarks>Reads <c>typeof(T)</c> &mdash; it does not touch <see cref="Value"/>, so nothing is boxed.</remarks>
    public string ValueType => typeof(T).Name;

    /// <inheritdoc />
    /// <remarks>
    /// <c>Value.ToString()</c> on a <c>T : struct</c> is a constrained virtual call
    /// (the compiler emits a <c>constrained.</c> prefix) &mdash; it invokes the value
    /// type's own <c>ToString</c> directly, without boxing. A type test such as
    /// <c>Value is IFormattable</c> is deliberately avoided here because testing a
    /// value type against an interface would box it.
    /// </remarks>
    public string FormatValue() => Value.ToString() ?? string.Empty;
}

/// <summary>
/// Convenience factory so callers can create a <see cref="TelemetryPacket{T}"/>
/// with <c>T</c> inferred from the argument, e.g.
/// <c>TelemetryPacket.Create("mac-1", 42.5f, "%")</c>.
/// </summary>
public static class TelemetryPacket
{
    public static TelemetryPacket<T> Create<T>(
        string sensorId,
        T value,
        string unit,
        DateTimeOffset? timestamp = null)
        where T : struct
        => new()
        {
            SensorId = sensorId,
            Value = value,
            Unit = unit,
            Timestamp = timestamp ?? DateTimeOffset.UtcNow,
        };
}
