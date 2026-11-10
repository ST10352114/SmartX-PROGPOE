using SmartXOne.Shared.Contracts;
using SmartXOne.Shared.Topology;

namespace SmartXOne.Api.Topology;

/// <summary>
/// Owns the live <see cref="MeshGraph"/> and its minimum spanning forest.
/// The forest is recomputed eagerly after every change (node, link or state),
/// so reads never pay for Prim's algorithm and every reader sees the same
/// routing. With a few dozen nodes a recompute takes microseconds.
/// <para>
/// It also remembers each node's previous route, which is how
/// <see cref="GetRoute"/> can say "rerouted around NODE-X" after a node drops out.
/// </para>
/// <para>One lock guards everything: the graph types aren't thread-safe.</para>
/// </summary>
public sealed class TopologyService(ILogger<TopologyService> logger)
{
    private readonly MeshGraph _graph = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RouteChange> _lastChange = new(StringComparer.OrdinalIgnoreCase);

    private SpanningForest _forest = new([], 0, new Dictionary<string, string>(), []);
    private Dictionary<string, IReadOnlyList<string>> _routes = new(StringComparer.OrdinalIgnoreCase);
    private long _version;
    private DateTimeOffset _updatedAt = DateTimeOffset.UtcNow;

    /// <exception cref="ArgumentException">A required field is blank or a value is out of range.</exception>
    /// <exception cref="InvalidOperationException">A different gateway is already registered.</exception>
    public MeshNodeDto AddOrReplaceNode(RegisterMeshNodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var node = new MeshNode(request.Mac, request.Name, request.Subnet, request.IsGateway, request.X, request.Y, request.Battery);
            lock (_gate)
            {
                if (node.IsGateway && _graph.Gateway is { } existing
                    && !string.Equals(existing.Mac, node.Mac, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Gateway '{existing.Mac}' is already registered; the mesh has one gateway.");
                }

                // Re-registering keeps the node's current online state.
                if (_graph.TryGetNode(node.Mac, out var previous))
                {
                    node.IsOnline = previous.IsOnline;
                }

                _graph.AddOrReplaceNode(node);
                RecomputeLocked($"node {node.Mac} registered");
                return ToDto(node);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Rejected mesh node {Mac}.", request.Mac);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to register mesh node {Mac}.", request.Mac);
            throw;
        }
    }

    /// <exception cref="ArgumentException">An end is blank, both ends are the same node, or the RSSI is out of range.</exception>
    /// <exception cref="KeyNotFoundException">Either end isn't a registered node.</exception>
    public MeshLinkDto AddOrUpdateLink(RegisterMeshLinkRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            lock (_gate)
            {
                _graph.AddOrUpdateLink(request.A, request.B, request.Rssi);
                RecomputeLocked($"link {request.A}-{request.B} registered");
                var link = _graph.LinksOf(request.A)
                    .First(l => string.Equals(MeshGraph.OtherEnd(l, _graph.GetNode(request.A).Mac), _graph.GetNode(request.B).Mac, StringComparison.OrdinalIgnoreCase));
                return ToDto(link);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
        {
            logger.LogWarning(ex, "Rejected mesh link {A}-{B}.", request.A, request.B);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to register mesh link {A}-{B}.", request.A, request.B);
            throw;
        }
    }

    /// <exception cref="KeyNotFoundException">No node has this MAC.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The battery is outside 0-100.</exception>
    /// <exception cref="InvalidOperationException">An attempt was made to take the gateway offline.</exception>
    public MeshNodeDto SetNodeState(string mac, SetMeshNodeStateRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mac);
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            lock (_gate)
            {
                var node = _graph.GetNode(mac);
                if (request.Online is false && node.IsGateway)
                {
                    // Every route ends at the gateway; "offline" would just mean "no mesh".
                    throw new InvalidOperationException("The gateway can't be taken offline.");
                }

                if (request.Battery is { } battery)
                {
                    node.Battery = battery;
                }

                if (request.Online is { } online)
                {
                    node.IsOnline = online;
                }

                RecomputeLocked($"node {node.Mac} {(node.IsOnline ? "online" : "offline")}, battery {node.Battery}%");
                return ToDto(node);
            }
        }
        catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Rejected state change for mesh node {Mac}.", mac);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to change state of mesh node {Mac}.", mac);
            throw;
        }
    }

    public TopologyDto GetTopology()
    {
        try
        {
            lock (_gate)
            {
                var nodes = _graph.Nodes
                    .OrderByDescending(n => n.IsGateway)
                    .ThenBy(n => n.Subnet, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(n => n.Mac, StringComparer.OrdinalIgnoreCase)
                    .Select(ToDto)
                    .ToList();

                var links = _graph.Links.Select(ToDto).ToList();
                return new TopologyDto(_graph.Gateway?.Mac, nodes, links, _version, _updatedAt);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read the mesh topology.");
            throw;
        }
    }

    public SpanningTreeDto GetSpanningTree()
    {
        try
        {
            lock (_gate)
            {
                var offline = _graph.Nodes.Where(n => !n.IsOnline).Select(n => n.Mac).Order(StringComparer.OrdinalIgnoreCase).ToList();
                var isolated = _graph.Nodes
                    .Where(n => n.IsOnline && !IsConnectedToGatewayLocked(n.Mac))
                    .Select(n => n.Mac)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new SpanningTreeDto(
                    _forest.Edges.Select(e => new SpanningEdgeDto(e.From, e.To, Math.Round(e.Cost, 3))).ToList(),
                    Math.Round(_forest.TotalCost, 3),
                    _forest.Roots,
                    isolated,
                    offline,
                    _version);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read the spanning tree.");
            throw;
        }
    }

    /// <exception cref="KeyNotFoundException">No node has this MAC.</exception>
    public MeshRouteDto GetRoute(string mac)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mac);

        try
        {
            lock (_gate)
            {
                var node = _graph.GetNode(mac);
                _lastChange.TryGetValue(node.Mac, out var change);
                var previous = change?.Previous ?? [];
                var avoided = change?.Avoided ?? [];

                if (_graph.Gateway is not { } gateway)
                {
                    return new MeshRouteDto(node.Mac, false, [], 0, null, false, previous, avoided, "No gateway is registered yet.");
                }

                if (!node.IsOnline)
                {
                    return new MeshRouteDto(node.Mac, false, [], 0, null, false, previous, avoided, $"{node.Name} is offline.");
                }

                if (!_routes.TryGetValue(node.Mac, out var path))
                {
                    return new MeshRouteDto(node.Mac, false, [], 0, null, false, previous, avoided,
                        $"{node.Name} is online but has no route to the gateway - its subnet is isolated.");
                }

                var fewestHops = MeshAlgorithms.FewestHopPath(_graph, gateway.Mac, node.Mac);
                var rerouted = avoided.Count > 0;
                var message = rerouted
                    ? $"Rerouted around {string.Join(", ", avoided)} ({path.Count - 1} hop(s))."
                    : $"{path.Count - 1} hop(s) to the gateway.";

                return new MeshRouteDto(node.Mac, true, path, Math.Round(PathCostLocked(path), 3), fewestHops, rerouted, previous, avoided, message);
            }
        }
        catch (KeyNotFoundException ex)
        {
            logger.LogWarning(ex, "Route requested for unknown mesh node {Mac}.", mac);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to compute the route for {Mac}.", mac);
            throw;
        }
    }

    /// <summary>
    /// BFS reachability from a node or from a whole subnet. <paramref name="from"/>
    /// is tried as a MAC first, then as a subnet name; when omitted the gateway is used.
    /// For a subnet, a node counts as reachable if any online node in the subnet reaches it,
    /// and its hop count is the smallest from any of them.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Neither a node nor a subnet matches, or no gateway exists and <paramref name="from"/> is omitted.</exception>
    public ReachabilityDto GetReachability(string? from)
    {
        try
        {
            lock (_gate)
            {
                var label = string.IsNullOrWhiteSpace(from)
                    ? _graph.Gateway?.Mac ?? throw new KeyNotFoundException("No gateway is registered yet.")
                    : from.Trim();

                List<string> starts;
                if (_graph.TryGetNode(label, out var startNode))
                {
                    starts = [startNode.Mac];
                }
                else
                {
                    starts = _graph.Nodes
                        .Where(n => string.Equals(n.Subnet, label, StringComparison.OrdinalIgnoreCase))
                        .Select(n => n.Mac)
                        .ToList();
                    if (starts.Count == 0)
                    {
                        throw new KeyNotFoundException($"'{label}' is neither a node MAC nor a subnet name.");
                    }
                }

                var best = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var start in starts)
                {
                    foreach (var (mac, hops) in MeshAlgorithms.Reachability(_graph, start).Reachable)
                    {
                        best[mac] = best.TryGetValue(mac, out var current) ? Math.Min(current, hops) : hops;
                    }
                }

                var reachable = best
                    .OrderBy(pair => pair.Value)
                    .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => new ReachableNodeDto(pair.Key, pair.Value))
                    .ToList();
                var unreachable = _graph.Nodes
                    .Where(n => !best.ContainsKey(n.Mac))
                    .Select(n => n.Mac)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new ReachabilityDto(label, reachable, unreachable);
            }
        }
        catch (KeyNotFoundException ex)
        {
            logger.LogWarning(ex, "Reachability requested from unknown start {From}.", from);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Reachability sweep from {From} failed.", from);
            throw;
        }
    }

    public IReadOnlyList<MeshComponentDto> GetComponents()
    {
        try
        {
            lock (_gate)
            {
                return MeshAlgorithms.ConnectedComponents(_graph)
                    .Select(component => new MeshComponentDto(
                        component.Order(StringComparer.OrdinalIgnoreCase).ToList(),
                        component.Select(mac => _graph.GetNode(mac).Subnet).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList(),
                        component.Any(mac => _graph.GetNode(mac).IsGateway)))
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Connected-components sweep failed.");
            throw;
        }
    }

    /// <summary>Re-runs Prim's, rebuilds every node's route, and records which routes changed and why. Caller holds the lock.</summary>
    private void RecomputeLocked(string reason)
    {
        _forest = MeshAlgorithms.MinimumSpanningForest(_graph, _graph.Gateway?.Mac);

        var routes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (_graph.Gateway is { IsOnline: true } gateway)
        {
            foreach (var node in _graph.Nodes.Where(n => n.IsOnline))
            {
                var path = MeshAlgorithms.PathFromRoot(_forest, node.Mac);
                if (string.Equals(path[0], gateway.Mac, StringComparison.OrdinalIgnoreCase))
                {
                    routes[node.Mac] = path;
                }
            }
        }

        foreach (var (mac, newPath) in routes)
        {
            if (_routes.TryGetValue(mac, out var oldPath) && !oldPath.SequenceEqual(newPath, StringComparer.OrdinalIgnoreCase))
            {
                var avoided = oldPath
                    .Where(hop => _graph.TryGetNode(hop, out var n) && !n.IsOnline)
                    .ToList();
                _lastChange[mac] = new RouteChange(oldPath, avoided);
                if (avoided.Count > 0)
                {
                    logger.LogInformation("Route for {Mac} rerouted around {Avoided}: {Path}.",
                        mac, string.Join(", ", avoided), string.Join(" -> ", newPath));
                }
            }
        }

        _routes = routes;
        _version++;
        _updatedAt = DateTimeOffset.UtcNow;
        logger.LogDebug("Topology v{Version} recomputed ({Reason}): MST cost {Cost:0.###}, {Trees} tree(s).",
            _version, reason, _forest.TotalCost, _forest.Roots.Count);
    }

    private double PathCostLocked(IReadOnlyList<string> path)
    {
        var total = 0.0;
        for (var i = 1; i < path.Count; i++)
        {
            total += _forest.Edges.First(e =>
                string.Equals(e.From, path[i - 1], StringComparison.OrdinalIgnoreCase)
                && string.Equals(e.To, path[i], StringComparison.OrdinalIgnoreCase)).Cost;
        }

        return total;
    }

    private bool IsConnectedToGatewayLocked(string mac) => _routes.ContainsKey(mac);

    private MeshNodeDto ToDto(MeshNode node) => new(
        node.Mac, node.Name, node.Subnet, node.IsGateway, node.IsOnline, node.X, node.Y, node.Battery,
        IsConnectedToGatewayLocked(node.Mac));

    private MeshLinkDto ToDto(MeshLink link)
    {
        var a = _graph.GetNode(link.A);
        var b = _graph.GetNode(link.B);
        var inTree = _forest.Parent.TryGetValue(b.Mac, out var parentOfB) && string.Equals(parentOfB, a.Mac, StringComparison.OrdinalIgnoreCase)
                     || _forest.Parent.TryGetValue(a.Mac, out var parentOfA) && string.Equals(parentOfA, b.Mac, StringComparison.OrdinalIgnoreCase);

        return new MeshLinkDto(a.Mac, b.Mac, link.Rssi, Math.Round(a.DistanceTo(b), 2), Math.Round(_graph.CostOf(link), 3),
            a.IsOnline && b.IsOnline, inTree);
    }

    private sealed record RouteChange(IReadOnlyList<string> Previous, IReadOnlyList<string> Avoided);
}
