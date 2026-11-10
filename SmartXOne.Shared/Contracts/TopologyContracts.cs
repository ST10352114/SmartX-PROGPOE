namespace SmartXOne.Shared.Contracts;

/// <summary>Adds a mesh node, or replaces the one with the same MAC (its links are kept).</summary>
/// <param name="X">Position on the site plan in metres - feeds the link cost.</param>
/// <param name="Battery">Charge 0-100 %; low battery makes links through the node more expensive.</param>
public record RegisterMeshNodeRequest(string Mac, string Name, string Subnet, bool IsGateway, double X, double Y, int Battery = 100);

/// <summary>Adds an undirected link between two registered nodes, or updates its signal strength.</summary>
/// <param name="Rssi">Received signal strength in dBm, -120 to 0.</param>
public record RegisterMeshLinkRequest(string A, string B, int Rssi);

/// <summary>Changes a node's live state; omitted fields are left as they are.</summary>
public record SetMeshNodeStateRequest(bool? Online, int? Battery);

/// <param name="ConnectedToGateway"><c>true</c> when the node is online and has a route to the gateway.</param>
public record MeshNodeDto(
    string Mac,
    string Name,
    string Subnet,
    bool IsGateway,
    bool IsOnline,
    double X,
    double Y,
    int Battery,
    bool ConnectedToGateway);

/// <param name="Cost">Routing cost from distance, RSSI and battery - see <c>MeshGraph.CostOf</c>.</param>
/// <param name="Usable"><c>false</c> when either end is offline.</param>
/// <param name="InSpanningTree"><c>true</c> when the minimum spanning tree uses this link.</param>
public record MeshLinkDto(string A, string B, int Rssi, double DistanceMetres, double Cost, bool Usable, bool InSpanningTree);

/// <param name="Version">Increases on every change, so a poller can tell whether anything moved.</param>
public record TopologyDto(
    string? GatewayMac,
    IReadOnlyList<MeshNodeDto> Nodes,
    IReadOnlyList<MeshLinkDto> Links,
    long Version,
    DateTimeOffset UpdatedAt);

public record SpanningEdgeDto(string From, string To, double Cost);

/// <param name="Roots">One root per tree: the gateway's tree first, then any isolated pockets.</param>
/// <param name="IsolatedNodes">Online nodes with no route to the gateway.</param>
public record SpanningTreeDto(
    IReadOnlyList<SpanningEdgeDto> Edges,
    double TotalCost,
    IReadOnlyList<string> Roots,
    IReadOnlyList<string> IsolatedNodes,
    IReadOnlyList<string> OfflineNodes,
    long Version);

/// <summary>How a node's traffic reaches the gateway.</summary>
/// <param name="Path">Gateway-first path through the minimum spanning tree; empty when unreachable.</param>
/// <param name="PathCost">Sum of link costs along <paramref name="Path"/>.</param>
/// <param name="FewestHopPath">The BFS path with the fewest hops (ignoring cost), for comparison; <c>null</c> when unreachable.</param>
/// <param name="Rerouted"><c>true</c> when the route last changed to avoid a node that has gone offline.</param>
/// <param name="PreviousPath">The route before the last change; empty if it has never changed.</param>
/// <param name="AvoidedNodes">Offline nodes the previous route went through.</param>
public record MeshRouteDto(
    string Mac,
    bool Reachable,
    IReadOnlyList<string> Path,
    double PathCost,
    IReadOnlyList<string>? FewestHopPath,
    bool Rerouted,
    IReadOnlyList<string> PreviousPath,
    IReadOnlyList<string> AvoidedNodes,
    string Message);

public record ReachableNodeDto(string Mac, int Hops);

/// <param name="From">The MAC or subnet name the sweep started from.</param>
public record ReachabilityDto(string From, IReadOnlyList<ReachableNodeDto> Reachable, IReadOnlyList<string> Unreachable);

public record MeshComponentDto(IReadOnlyList<string> Macs, IReadOnlyList<string> Subnets, bool ContainsGateway);
