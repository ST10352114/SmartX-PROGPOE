using SmartXOne.Shared.Deployment;
using SmartXOne.Shared.Telemetry;

namespace SmartXOne.Shared.Demo;

/// <summary>Result of <see cref="SharedFeatureDemo.Run"/> &mdash; one line per PoE requirement.</summary>
public sealed record SharedFeatureDemoReport(
    string Generics,
    string OperatorOverloading,
    string AdvancedArrays,
    string Recursion);

/// <summary>
/// Exercises each of the four assessed C# features end to end so they can be
/// demonstrated live (the API exposes this at <c>GET /api/demo/oop</c> and also
/// logs it at start-up). Nothing here is required by the running app &mdash; it
/// is a guided tour of the real types in <c>SmartX.Shared</c>.
/// </summary>
public static class SharedFeatureDemo
{
    public static SharedFeatureDemoReport Run() => new(
        Generics: DemoGenerics(),
        OperatorOverloading: DemoOperatorOverloading(),
        AdvancedArrays: DemoAdvancedArrays(),
        Recursion: DemoRecursion());

    // 3a - GENERICS
    private static string DemoGenerics()
    {
        // One generic type carries three different value types, each stays typed.
        // Source attribution: Microsoft .NET Observability with OpenTelemetry Documentation
        // URL: https://learn.microsoft.com/en-us/dotnet/core/diagnostics/observability-with-otel
        TelemetryPacket<float> moisture = TelemetryPacket.Create("soil-01", 42.7f, "%");
        TelemetryPacket<int> wattage = TelemetryPacket.Create("meter-01", 1350, "W");
        TelemetryPacket<bool> valve = TelemetryPacket.Create("valve-01", true, "state");

        float typedBack = moisture.Value; // still a float, no cast, no unbox

        return $"TelemetryPacket<T>: <float>={moisture.FormatValue()}{moisture.Unit} " +
               $"(round-trips to float {typedBack}), <int>={wattage.FormatValue()}{wattage.Unit}, " +
               $"<bool>={valve.FormatValue()}. ValueType tags: {moisture.ValueType}/{wattage.ValueType}/{valve.ValueType}.";
    }

    // 3b - OPERATOR OVERLOADING
    private static string DemoOperatorOverloading()
    {
        var meterA = new SensorReading("meter-A", 1200, "W");
        var meterB = new SensorReading("meter-B", 900, "W");
        var previousA = new SensorReading("meter-A(prev)", 1000, "W");

        SensorReading combinedLoad = meterA + meterB;         // aggregate
        SensorReading jump = meterA - previousA;              // delta vs previous sample
        bool spiked = jump > new SensorReading("threshold", 150, "W"); // comparison

        return $"combined load (meterA + meterB) = {combinedLoad.Value} W; " +
               $"delta (meterA - previous) = {jump.Value} W; " +
               $"delta > 150 W threshold => spike alert = {spiked}.";
    }

    // 3c - ADVANCED ARRAYS
    private static string DemoAdvancedArrays()
    {
        var t0 = DateTimeOffset.UtcNow;

        // Stage 1: jagged array batches[deviceIndex][readingIndex].
        var buffer = new TelemetryBatchBuffer<int>(deviceCount: 2);
        buffer.SetDeviceBatch(0,
        [
            TelemetryPacket.Create("meter-01", 1000, "W", t0.AddSeconds(0)),
            TelemetryPacket.Create("meter-01", 1120, "W", t0.AddSeconds(10)),
            TelemetryPacket.Create("meter-01", 1080, "W", t0.AddSeconds(20)),
        ]);
        buffer.SetDeviceBatch(1,
        [
            TelemetryPacket.Create("meter-02", 500, "W", t0.AddSeconds(5)),
            TelemetryPacket.Create("meter-02", 540, "W", t0.AddSeconds(15)),
        ]);

        var firstDeviceSecondReading = buffer.Batches[0][1].Value; // jagged indexing

        // Stage 2: convert to an optimised, timestamp-ordered List<T>.
        List<TelemetryPacket<int>> ongoing = buffer.ToOptimisedList();

        return $"jagged buffer: device0 has {buffer.Batches[0].Count} readings, " +
               $"device1 has {buffer.Batches[1].Count}; batches[0][1] = {firstDeviceSecondReading} W. " +
               $"Flattened to List<TelemetryPacket<int>> of {ongoing.Count} readings ordered by time " +
               $"(first = {ongoing[0].FormatValue()} W, last = {ongoing[^1].FormatValue()} W).";
    }

    // 3d - RECURSION
    private static string DemoRecursion()
    {
        // Facility -> Zone -> Sub-Zone -> Node
        var facility = new DeploymentNode("FAC-1", "Riverside Hydroponics", DeploymentLevel.Facility);
        var zone = new DeploymentNode("ZONE-A", "Greenhouse A", DeploymentLevel.Zone);
        var subZone = new DeploymentNode("SZ-A3", "Row 3 drip line", DeploymentLevel.SubZone);
        var node = new DeploymentNode("NODE-A3-07", "Mounting point 7", DeploymentLevel.Node);

        facility.AddChild(zone.AddChild(subZone.AddChild(node)));
        facility.AddChild(new DeploymentNode("ZONE-B", "Greenhouse B", DeploymentLevel.Zone));

        bool present = DeploymentTreeValidator.ContainsNode(facility, "NODE-A3-07");
        bool missing = DeploymentTreeValidator.ContainsNode(facility, "NODE-X9-99");

        // Deliberately create a cycle (leaf points back to the root) and confirm
        // the search still terminates instead of overflowing the stack.
        node.AddChild(facility);
        bool cyclicHandled;
        try
        {
            DeploymentTreeValidator.ContainsNode(facility, "NODE-X9-99");
            cyclicHandled = true; // returned false without looping forever
        }
        catch (InvalidOperationException)
        {
            cyclicHandled = true; // or bailed out via the depth cap - either is safe
        }

        return $"tree Facility>Zone>SubZone>Node: ContainsNode('NODE-A3-07') = {present}, " +
               $"ContainsNode('NODE-X9-99') = {missing}. Base case for null, a visited-set " +
               $"for cycles (cyclic input handled safely = {cyclicHandled}), and a depth cap " +
               $"of {DeploymentTreeValidator.MaxDepth}.";
    }
}
