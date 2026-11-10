using SmartXOne.Shared.Collections;

namespace SmartXOne.Shared.Topology;

/// <summary>Result of a breadth-first sweep: who can be reached from the start node, and how many hops away.</summary>
/// <param name="Reachable">Online nodes reachable from the start, in BFS (nearest-first) order, with their hop count.</param>
/// <param name="Unreachable">Every other node - offline, or online but cut off from the start.</param>
public sealed record ReachabilityResult(
    string Start,
    IReadOnlyList<(string Mac, int Hops)> Reachable,
    IReadOnlyList<string> Unreachable);

/// <summary>One link chosen for the spanning tree, oriented parent -> child.</summary>
public sealed record SpanningEdge(string From, string To, double Cost);

/// <summary>
/// Output of <see cref="MeshAlgorithms.MinimumSpanningForest"/>: one tree for
/// the gateway's component plus one per isolated pocket of online nodes.
/// </summary>
/// <param name="Parent">Each node's parent in its tree (tree roots are absent), so following it gives the route back to the root.</param>
/// <param name="Roots">The root of each tree; the gateway's tree comes first when the gateway is online.</param>
public sealed record SpanningForest(
    IReadOnlyList<SpanningEdge> Edges,
    double TotalCost,
    IReadOnlyDictionary<string, string> Parent,
    IReadOnlyList<string> Roots);

// Code Attribution
// Purpose: Queue-based breadth-first search - reachability and fewest-hop paths
// Author: GeeksforGeeks
// Title: Breadth First Search or BFS for a Graph
// Year: 2026
// URL/DOI: https://www.geeksforgeeks.org/dsa/breadth-first-search-or-bfs-for-a-graph/
// Accessed: 10 November 2026
//
// Code Attribution
// Purpose: Depth-first search with an explicit stack instead of recursion, used for connected components
// Author: GeeksforGeeks
// Title: Iterative Depth First Traversal of Graph
// Year: 2025
// URL/DOI: https://www.geeksforgeeks.org/dsa/iterative-depth-first-traversal/
// Accessed: 10 November 2026
//
// Code Attribution
// Purpose: Prim's minimum spanning tree driven by a min-heap of candidate edges
// Author: GeeksforGeeks
// Title: Prim's Algorithm for Minimum Spanning Tree (MST)
// Year: 2026
// URL/DOI: https://www.geeksforgeeks.org/dsa/prims-minimum-spanning-tree-mst-greedy-algo-5/
// Accessed: 10 November 2026

/// <summary>
/// Graph algorithms over a <see cref="MeshGraph"/>. Offline nodes are treated
/// as absent: nothing is routed through them, and they're never "reachable".
/// </summary>
public static class MeshAlgorithms
{
    /// <summary>
    /// Breadth-first sweep from <paramref name="startMac"/> using a
    /// <see cref="Queue{T}"/>: nodes come out in order of hop distance. O(V + E).
    /// </summary>
    /// <exception cref="KeyNotFoundException">The start node isn't in the graph.</exception>
    public static ReachabilityResult Reachability(MeshGraph graph, string startMac)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var start = graph.GetNode(startMac);

        var hops = BreadthFirst(graph, start.Mac, parent: null);
        var reachable = hops.Select(pair => (pair.Key, pair.Value)).ToList();
        var unreachable = graph.Nodes
            .Where(n => !hops.ContainsKey(n.Mac))
            .Select(n => n.Mac)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ReachabilityResult(start.Mac, reachable, unreachable);
    }

    /// <summary>
    /// The path with the fewest hops from <paramref name="fromMac"/> to
    /// <paramref name="toMac"/> over online nodes (BFS with parent pointers),
    /// ignoring link cost. <c>null</c> if there is no such path.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Either node isn't in the graph.</exception>
    public static IReadOnlyList<string>? FewestHopPath(MeshGraph graph, string fromMac, string toMac)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var from = graph.GetNode(fromMac);
        var to = graph.GetNode(toMac);

        var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hops = BreadthFirst(graph, from.Mac, parent);
        if (!hops.ContainsKey(to.Mac))
        {
            return null;
        }

        var path = new List<string> { to.Mac };
        while (parent.TryGetValue(path[^1], out var previous))
        {
            path.Add(previous);
        }

        path.Reverse();
        return path;
    }

    /// <summary>
    /// Groups the online nodes into connected components using an iterative
    /// depth-first search on a <see cref="Stack{T}"/> (no recursion, so a long
    /// chain of nodes can't overflow the call stack). O(V + E). The component
    /// containing the gateway, if any, is listed first.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> ConnectedComponents(MeshGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var components = new List<IReadOnlyList<string>>();

        // Gateway first so its component is components[0].
        var starts = graph.Nodes.Where(n => n.IsOnline).OrderByDescending(n => n.IsGateway).ToList();
        foreach (var startNode in starts)
        {
            if (!visited.Add(startNode.Mac))
            {
                continue;
            }

            var component = new List<string>();
            var stack = new Stack<string>();
            stack.Push(startNode.Mac);

            while (stack.Count > 0)
            {
                var mac = stack.Pop();
                component.Add(mac);

                foreach (var link in graph.LinksOf(mac))
                {
                    var next = MeshGraph.OtherEnd(link, mac);
                    if (graph.GetNode(next).IsOnline && visited.Add(next))
                    {
                        stack.Push(next);
                    }
                }
            }

            components.Add(component);
        }

        return components;
    }

    /// <summary>
    /// Lazy Prim's algorithm on a <see cref="MinHeap{T}"/> of candidate links.
    /// Grows a tree from <paramref name="rootMac"/> (normally the gateway) by
    /// repeatedly taking the cheapest link to a node not yet in the tree; stale
    /// heap entries whose far end has since joined are skipped when popped.
    /// When the heap runs dry, any online nodes still outside the tree form
    /// separate pockets, so Prim restarts from the next one - the result is a
    /// minimum spanning <em>forest</em> rather than silently dropping isolated subnets.
    /// O(E log E), i.e. O(E log V).
    /// </summary>
    /// <param name="rootMac">Where the first tree starts; if null or offline, trees start from online nodes in MAC order.</param>
    /// <exception cref="KeyNotFoundException"><paramref name="rootMac"/> is given but isn't in the graph.</exception>
    public static SpanningForest MinimumSpanningForest(MeshGraph graph, string? rootMac)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var inTree = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var edges = new List<SpanningEdge>();
        var roots = new List<string>();
        var total = 0.0;

        // Cost first; MACs break ties so the same mesh always gives the same tree.
        var heap = new MinHeap<SpanningEdge>(Comparer<SpanningEdge>.Create((x, y) =>
        {
            var byCost = x.Cost.CompareTo(y.Cost);
            if (byCost != 0)
            {
                return byCost;
            }

            var byFrom = StringComparer.OrdinalIgnoreCase.Compare(x.From, y.From);
            return byFrom != 0 ? byFrom : StringComparer.OrdinalIgnoreCase.Compare(x.To, y.To);
        }));

        var startOrder = new List<MeshNode>();
        if (rootMac is not null && graph.GetNode(rootMac) is { IsOnline: true } root)
        {
            startOrder.Add(root);
        }

        startOrder.AddRange(graph.Nodes.Where(n => n.IsOnline).OrderBy(n => n.Mac, StringComparer.OrdinalIgnoreCase));

        foreach (var start in startOrder)
        {
            if (!inTree.Add(start.Mac))
            {
                continue;
            }

            roots.Add(start.Mac);
            PushFrontier(graph, start.Mac, inTree, heap);

            while (heap.TryPop(out var candidate))
            {
                if (!inTree.Add(candidate.To))
                {
                    continue; // stale: reached more cheaply already
                }

                edges.Add(candidate);
                parent[candidate.To] = candidate.From;
                total += candidate.Cost;
                PushFrontier(graph, candidate.To, inTree, heap);
            }
        }

        return new SpanningForest(edges, total, parent, roots);
    }

    /// <summary>
    /// Follows <see cref="SpanningForest.Parent"/> from <paramref name="mac"/>
    /// up to its tree's root, returned root-first (e.g. gateway -> relay -> node).
    /// </summary>
    /// <exception cref="InvalidOperationException">The parent map contains a cycle (it never should).</exception>
    public static IReadOnlyList<string> PathFromRoot(SpanningForest forest, string mac)
    {
        ArgumentNullException.ThrowIfNull(forest);
        ArgumentException.ThrowIfNullOrWhiteSpace(mac);

        var path = new List<string> { mac };
        var guard = forest.Parent.Count + 1;
        while (forest.Parent.TryGetValue(path[^1], out var next))
        {
            if (--guard < 0)
            {
                throw new InvalidOperationException($"Spanning forest parent map loops while tracing '{mac}'.");
            }

            path.Add(next);
        }

        path.Reverse();
        return path;
    }

    private static void PushFrontier(MeshGraph graph, string mac, HashSet<string> inTree, MinHeap<SpanningEdge> heap)
    {
        foreach (var link in graph.LinksOf(mac))
        {
            var next = MeshGraph.OtherEnd(link, mac);
            if (!inTree.Contains(next) && graph.GetNode(next).IsOnline)
            {
                heap.Push(new SpanningEdge(mac, next, graph.CostOf(link)));
            }
        }
    }

    /// <summary>Shared BFS core: hop count per reached node, in visit order; fills <paramref name="parent"/> when given.</summary>
    private static Dictionary<string, int> BreadthFirst(MeshGraph graph, string startMac, Dictionary<string, string>? parent)
    {
        var hops = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!graph.GetNode(startMac).IsOnline)
        {
            return hops; // an offline node can't reach anything
        }

        var queue = new Queue<string>();
        hops[startMac] = 0;
        queue.Enqueue(startMac);

        while (queue.Count > 0)
        {
            var mac = queue.Dequeue();
            foreach (var link in graph.LinksOf(mac))
            {
                var next = MeshGraph.OtherEnd(link, mac);
                if (hops.ContainsKey(next) || !graph.GetNode(next).IsOnline)
                {
                    continue;
                }

                hops[next] = hops[mac] + 1;
                parent?.Add(next, mac);
                queue.Enqueue(next);
            }
        }

        return hops;
    }
}
