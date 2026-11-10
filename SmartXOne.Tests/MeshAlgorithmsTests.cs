using SmartXOne.Shared.Topology;

namespace SmartXOne.Tests;

public class MeshAlgorithmsTests
{
    // All nodes sit at the same point with full batteries, so a link's cost
    // is just 1 + (-RSSI - 50) / 10: -50 dBm -> 1, -60 -> 2, -70 -> 3, ...
    private static int RssiForCost(int cost) => -50 - ((cost - 1) * 10);

    private static MeshGraph Build(string gateway, IEnumerable<string> nodes, IEnumerable<(string A, string B, int Cost)> links)
    {
        var graph = new MeshGraph();
        graph.AddOrReplaceNode(new MeshNode(gateway, gateway, "core", isGateway: true, 0, 0));
        foreach (var mac in nodes)
        {
            graph.AddOrReplaceNode(new MeshNode(mac, mac, "field", isGateway: false, 0, 0));
        }

        foreach (var (a, b, cost) in links)
        {
            graph.AddOrUpdateLink(a, b, RssiForCost(cost));
        }

        return graph;
    }

    //   GW --1-- A --4-- C
    //    \       |       |
    //     3      2       1
    //      \     |       |
    //        --- B --5-- D        E (no links)
    private static MeshGraph SampleMesh() => Build(
        "GW",
        ["A", "B", "C", "D", "E"],
        [("GW", "A", 1), ("GW", "B", 3), ("A", "B", 2), ("A", "C", 4), ("B", "D", 5), ("C", "D", 1)]);

    [Fact]
    public void CostOf_CombinesDistanceSignalAndBattery()
    {
        var graph = new MeshGraph();
        graph.AddOrReplaceNode(new MeshNode("N1", "N1", "s", false, 0, 0, battery: 100));
        graph.AddOrReplaceNode(new MeshNode("N2", "N2", "s", false, 6, 8, battery: 75)); // 10 m away
        graph.AddOrUpdateLink("N1", "N2", -60);

        // 1 + 10/10 + (60-50)/10 + (100-75)/25 = 4
        Assert.Equal(4.0, graph.CostOf(graph.Links.Single()), precision: 9);
    }

    [Fact]
    public void Reachability_ListsNearestFirstAndReportsIsolatedNodes()
    {
        var result = MeshAlgorithms.Reachability(SampleMesh(), "GW");

        Assert.Equal("GW", result.Reachable[0].Mac);
        Assert.Equal(["A", "B"], result.Reachable.Where(r => r.Hops == 1).Select(r => r.Mac).Order());
        Assert.Equal(["C", "D"], result.Reachable.Where(r => r.Hops == 2).Select(r => r.Mac).Order());
        Assert.Equal(["E"], result.Unreachable);
    }

    [Fact]
    public void Reachability_SkipsOfflineNodes()
    {
        var graph = SampleMesh();
        graph.GetNode("A").IsOnline = false;
        graph.GetNode("B").IsOnline = false; // GW's only two neighbours

        var result = MeshAlgorithms.Reachability(graph, "GW");

        Assert.Equal(["GW"], result.Reachable.Select(r => r.Mac));
        Assert.Equal(["A", "B", "C", "D", "E"], result.Unreachable);
    }

    [Fact]
    public void FewestHopPath_IgnoresCost_AndReturnsNullWhenCutOff()
    {
        var graph = SampleMesh();

        Assert.Equal(["GW", "A", "C"], MeshAlgorithms.FewestHopPath(graph, "GW", "C"));
        Assert.Null(MeshAlgorithms.FewestHopPath(graph, "GW", "E"));
    }

    [Fact]
    public void ConnectedComponents_PutsGatewayComponentFirst()
    {
        var graph = SampleMesh();
        graph.GetNode("A").IsOnline = false;
        graph.GetNode("B").IsOnline = false;

        var components = MeshAlgorithms.ConnectedComponents(graph);

        Assert.Equal(["GW"], components[0]);
        Assert.Contains(components, c => c.Order().SequenceEqual(["C", "D"]));
        Assert.Contains(components, c => c.SequenceEqual(["E"]));
        Assert.Equal(3, components.Count);
    }

    [Fact]
    public void MinimumSpanningForest_PicksTheCheapestTree()
    {
        var forest = MeshAlgorithms.MinimumSpanningForest(SampleMesh(), "GW");

        // GW-A 1, A-B 2, A-C 4, C-D 1 = 8 (E is its own one-node tree)
        Assert.Equal(8.0, forest.TotalCost, precision: 9);
        Assert.Equal(4, forest.Edges.Count);
        Assert.Equal(["GW", "E"], forest.Roots);
        Assert.Equal(["GW", "A", "C", "D"], MeshAlgorithms.PathFromRoot(forest, "D"));
    }

    [Fact]
    public void MinimumSpanningForest_ReroutesAroundAnOfflineNode()
    {
        var graph = SampleMesh();
        graph.GetNode("A").IsOnline = false;

        var forest = MeshAlgorithms.MinimumSpanningForest(graph, "GW");

        Assert.Equal(["GW", "B", "D", "C"], MeshAlgorithms.PathFromRoot(forest, "C"));
        Assert.DoesNotContain(forest.Edges, e => e.From == "A" || e.To == "A");
    }

    [Fact]
    public void MinimumSpanningForest_MatchesKruskalOnRandomMeshes()
    {
        var random = new Random(2026);
        for (var trial = 0; trial < 25; trial++)
        {
            var nodes = Enumerable.Range(1, 12).Select(i => $"N{i}").ToList();
            var links = new List<(string, string, int)>();
            for (var i = 0; i < nodes.Count; i++)
            {
                for (var j = i + 1; j < nodes.Count; j++)
                {
                    if (random.NextDouble() < 0.35)
                    {
                        links.Add((nodes[i], nodes[j], random.Next(1, 8)));
                    }
                }
            }

            links.Add(("GW", nodes[random.Next(nodes.Count)], random.Next(1, 8)));
            var graph = Build("GW", nodes, links);

            var prim = MeshAlgorithms.MinimumSpanningForest(graph, "GW");

            Assert.Equal(KruskalTotal(graph), prim.TotalCost, precision: 9);
            Assert.Equal(graph.NodeCount - prim.Roots.Count, prim.Edges.Count);
        }
    }

    [Fact]
    public void UnknownNode_Throws()
    {
        var graph = SampleMesh();

        Assert.Throws<KeyNotFoundException>(() => MeshAlgorithms.Reachability(graph, "nope"));
        Assert.Throws<KeyNotFoundException>(() => graph.AddOrUpdateLink("GW", "nope", -60));
    }

    [Fact]
    public void AddOrUpdateLink_RejectsSelfLinksAndUpdatesExistingLinks()
    {
        var graph = SampleMesh();

        Assert.Throws<ArgumentException>(() => graph.AddOrUpdateLink("A", "a", -60));

        graph.AddOrUpdateLink("A", "GW", -90); // same pair, reversed
        Assert.Equal(-90, graph.Links.Single(l => (l.A, l.B) is ("GW", "A") or ("A", "GW")).Rssi);
        Assert.Equal(6, graph.Links.Count());
    }

    /// <summary>Independent reference: Kruskal with union-find over online links.</summary>
    private static double KruskalTotal(MeshGraph graph)
    {
        var parent = graph.Nodes.ToDictionary(n => n.Mac, n => n.Mac);
        string Find(string x) => parent[x] == x ? x : parent[x] = Find(parent[x]);

        var total = 0.0;
        foreach (var link in graph.Links.OrderBy(graph.CostOf))
        {
            var (ra, rb) = (Find(link.A), Find(link.B));
            if (ra != rb)
            {
                parent[ra] = rb;
                total += graph.CostOf(link);
            }
        }

        return total;
    }
}
