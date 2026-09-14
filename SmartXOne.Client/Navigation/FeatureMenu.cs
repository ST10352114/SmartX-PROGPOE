namespace SmartXOne.Client.Navigation;

/// <summary>
/// One entry in the Smart-X feature menu. A single source of truth shared by the
/// sidebar (<c>NavMenu</c>) and the landing page cards (<c>Home</c>) so the two
/// never drift apart.
/// </summary>
/// <param name="Title">Menu label.</param>
/// <param name="Description">One-line explanation shown on the landing card.</param>
/// <param name="Href">Route to navigate to when the item is enabled.</param>
/// <param name="Enabled">False renders a greyed-out, non-clickable placeholder.</param>
/// <param name="StatusLabel">Badge text for disabled items, e.g. "Coming in Part 2".</param>
public record FeatureMenuItem(
    string Title,
    string Description,
    string Href,
    bool Enabled,
    string? StatusLabel);

public static class FeatureMenu
{
    /// <summary>The three PoE features. Only the first is built in Part 1.</summary>
    public static readonly IReadOnlyList<FeatureMenuItem> Items =
    [
        new FeatureMenuItem(
            Title: "Sensor Data Ingestion and Telemetry",
            Description: "Register mesh sensors, ingest multi-typed telemetry packets, attach config/photo/log files, and watch live readings.",
            Href: "ingestion",
            Enabled: true,
            StatusLabel: null),

        new FeatureMenuItem(
            Title: "Real-Time Command Stream and History",
            Description: "Push commands to actuator nodes and replay the command/acknowledgement history.",
            Href: "command-stream",
            Enabled: false,
            StatusLabel: "Coming in Part 2"),

        new FeatureMenuItem(
            Title: "Network Topology and Mesh Routing",
            Description: "Visualise the node mesh and compute routing paths between gateway and leaf nodes.",
            Href: "topology",
            Enabled: false,
            StatusLabel: "Coming in Part 3"),
    ];
}
