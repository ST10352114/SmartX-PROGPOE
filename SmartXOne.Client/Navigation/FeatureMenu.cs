namespace SmartXOne.Client.Navigation;

/// <summary>
/// One entry in the Smart-X feature menu. A single source of truth shared by the
/// sidebar (<c>NavMenu</c>) and the landing page cards (<c>Home</c>) so the two
/// never drift apart.
/// </summary>
/// <param name="Title">Menu label.</param>
/// <param name="Description">One-line explanation shown on the landing card.</param>
/// <param name="Href">Route to navigate to.</param>
public record FeatureMenuItem(
    string Title,
    string Description,
    string Href);

public static class FeatureMenu
{
    /// <summary>The features that are built and reachable. Only working pages are listed - no placeholders.</summary>
    public static readonly IReadOnlyList<FeatureMenuItem> Items =
    [
        new FeatureMenuItem(
            Title: "Sensor Data Ingestion and Telemetry",
            Description: "Register mesh sensors, ingest multi-typed telemetry packets, attach config/photo/log files, and watch live readings.",
            Href: "ingestion"),

        new FeatureMenuItem(
            Title: "Real-Time Command Stream and History",
            Description: "Live device telemetry, manual pump/relay/valve overrides with undo, and the command history behind them.",
            Href: "command-stream"),

        new FeatureMenuItem(
            Title: "Network Topology and Routing",
            Description: "See the mesh as a graph, follow the minimum spanning tree to the gateway, search routes by MAC and take nodes offline to watch traffic reroute.",
            Href: "topology"),
    ];
}
