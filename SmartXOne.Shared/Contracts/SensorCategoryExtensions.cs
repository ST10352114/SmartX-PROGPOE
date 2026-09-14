namespace SmartXOne.Shared.Contracts;

/// <summary>
/// The default unit each <see cref="SensorCategory"/> reports in. Shared by the
/// API (falls back to this when a reading omits <c>Unit</c>) and the client
/// (can show it as a placeholder/label next to the input).
/// </summary>
public static class SensorCategoryExtensions
{
    public static string DefaultUnit(this SensorCategory category) => category switch
    {
        SensorCategory.Environmental => "%",
        SensorCategory.PowerConsumption => "W",
        SensorCategory.Actuator => "state",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, message: null),
    };
}
