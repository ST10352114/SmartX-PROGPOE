namespace SmartXOne.Shared.Topology;

/// <summary>One node in the mesh. Position and battery feed the link cost; <see cref="IsOnline"/> decides whether routing may use it.</summary>
public sealed class MeshNode
{
    /// <exception cref="ArgumentException"><paramref name="mac"/>, <paramref name="name"/> or <paramref name="subnet"/> is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="battery"/> is outside 0-100, or a coordinate isn't finite.</exception>
    public MeshNode(string mac, string name, string subnet, bool isGateway, double x, double y, int battery = 100, bool isOnline = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mac);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(subnet);
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Node coordinates must be finite numbers.");
        }

        Mac = mac.Trim();
        Name = name.Trim();
        Subnet = subnet.Trim();
        IsGateway = isGateway;
        X = x;
        Y = y;
        Battery = battery;
        IsOnline = isOnline;
    }

    public string Mac { get; }

    public string Name { get; }

    public string Subnet { get; }

    public bool IsGateway { get; }

    /// <summary>Position on the site plan, in metres.</summary>
    public double X { get; }

    public double Y { get; }

    public bool IsOnline { get; set; }

    /// <summary>Battery charge, 0-100 %.</summary>
    public int Battery
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(Battery));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 100, nameof(Battery));
            field = value;
        }
    }

    public double DistanceTo(MeshNode other) => Math.Sqrt(Math.Pow(X - other.X, 2) + Math.Pow(Y - other.Y, 2));
}

/// <summary>An undirected radio link between two nodes, with its measured signal strength.</summary>
/// <param name="Rssi">Received signal strength in dBm (negative; closer to 0 is stronger).</param>
public sealed record MeshLink(string A, string B, int Rssi);

/// <summary>
/// The mesh as an undirected weighted graph: a node dictionary keyed by MAC
/// plus an adjacency list (each link stored once per endpoint). Costs are
/// computed on demand from the current node state, so a battery dropping or a
/// node moving is reflected the next time routing runs.
/// <para>Not thread-safe: the API's TopologyService locks around it.</para>
/// </summary>
public sealed class MeshGraph
{
    public const int MinRssi = -120;
    public const int MaxRssi = 0;

    private readonly Dictionary<string, MeshNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<MeshLink>> _adjacency = new(StringComparer.OrdinalIgnoreCase);

    public int NodeCount => _nodes.Count;

    public IEnumerable<MeshNode> Nodes => _nodes.Values;

    /// <summary>Every link once (not once per endpoint).</summary>
    public IEnumerable<MeshLink> Links =>
        _adjacency.SelectMany(pair => pair.Value.Where(link => string.Equals(link.A, pair.Key, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The first gateway node, if one has been added.</summary>
    public MeshNode? Gateway => _nodes.Values.FirstOrDefault(n => n.IsGateway);

    /// <summary>Adds a node, or replaces the node with the same MAC (its links are kept).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> is null.</exception>
    public void AddOrReplaceNode(MeshNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _nodes[node.Mac] = node;
        _adjacency.TryAdd(node.Mac, []);
    }

    public bool TryGetNode(string mac, out MeshNode node) => _nodes.TryGetValue(mac, out node!);

    /// <exception cref="KeyNotFoundException">No node has this MAC.</exception>
    public MeshNode GetNode(string mac) =>
        _nodes.TryGetValue(mac, out var node) ? node : throw new KeyNotFoundException($"No mesh node with MAC '{mac}'.");

    /// <summary>Links <paramref name="a"/> and <paramref name="b"/>, or updates the RSSI of an existing link between them.</summary>
    /// <exception cref="ArgumentException">Both ends are the same node.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rssi"/> is outside <see cref="MinRssi"/>..<see cref="MaxRssi"/>.</exception>
    /// <exception cref="KeyNotFoundException">Either MAC isn't a node in the graph.</exception>
    public void AddOrUpdateLink(string a, string b, int rssi)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(a);
        ArgumentException.ThrowIfNullOrWhiteSpace(b);
        ArgumentOutOfRangeException.ThrowIfLessThan(rssi, MinRssi);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rssi, MaxRssi);

        var nodeA = GetNode(a);
        var nodeB = GetNode(b);
        if (ReferenceEquals(nodeA, nodeB))
        {
            throw new ArgumentException($"A node can't link to itself ('{a}').", nameof(b));
        }

        _adjacency[nodeA.Mac].RemoveAll(l => IsBetween(l, nodeA.Mac, nodeB.Mac));
        _adjacency[nodeB.Mac].RemoveAll(l => IsBetween(l, nodeA.Mac, nodeB.Mac));

        var link = new MeshLink(nodeA.Mac, nodeB.Mac, rssi);
        _adjacency[nodeA.Mac].Add(link);
        _adjacency[nodeB.Mac].Add(link);
    }

    /// <summary>Links touching <paramref name="mac"/>, in the order they were added.</summary>
    public IReadOnlyList<MeshLink> LinksOf(string mac) =>
        _adjacency.TryGetValue(mac, out var links) ? links : [];

    /// <summary>The MAC at the other end of <paramref name="link"/> from <paramref name="mac"/>.</summary>
    public static string OtherEnd(MeshLink link, string mac) =>
        string.Equals(link.A, mac, StringComparison.OrdinalIgnoreCase) ? link.B : link.A;

    /// <summary>
    /// Routing cost of a link: <c>1 + distance/10 + max(0, -RSSI - 50)/10 + (100 - min(battery))/25</c>.
    /// Every hop costs at least 1, then longer links, signal weaker than
    /// -50 dBm and a low battery at either end each add to it - so routing
    /// prefers short, strong links between well-charged nodes.
    /// </summary>
    /// <example>10 m, -60 dBm, batteries 100 % and 75 %: 1 + 1 + 1 + 1 = 4.</example>
    public double CostOf(MeshLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var a = GetNode(link.A);
        var b = GetNode(link.B);

        return 1
               + a.DistanceTo(b) / 10
               + Math.Max(0, -link.Rssi - 50) / 10.0
               + (100 - Math.Min(a.Battery, b.Battery)) / 25.0;
    }

    private static bool IsBetween(MeshLink link, string a, string b) =>
        (string.Equals(link.A, a, StringComparison.OrdinalIgnoreCase) && string.Equals(link.B, b, StringComparison.OrdinalIgnoreCase))
        || (string.Equals(link.A, b, StringComparison.OrdinalIgnoreCase) && string.Equals(link.B, a, StringComparison.OrdinalIgnoreCase));
}
