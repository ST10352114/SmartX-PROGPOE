using SmartXOne.Shared.Collections;

namespace SmartXOne.Tests;

public class AvlTreeTests
{
    [Theory]
    [InlineData(new[] { 3, 2, 1 })] // LL: right rotation
    [InlineData(new[] { 1, 2, 3 })] // RR: left rotation
    [InlineData(new[] { 3, 1, 2 })] // LR: left then right rotation
    [InlineData(new[] { 1, 3, 2 })] // RL: right then left rotation
    public void EachRotationCase_LeavesThreeNodesBalancedAtHeightTwo(int[] keys)
    {
        var tree = new AvlTree<int, string>();
        foreach (var key in keys)
        {
            tree.Add(key, $"v{key}");
        }

        Assert.Equal(2, tree.Height);
        Assert.True(tree.IsBalancedSearchTree());
        Assert.Equal([1, 2, 3], tree.InOrder().Select(e => e.Key));
    }

    [Fact]
    public void AscendingInserts_StayLogarithmic()
    {
        // Timestamps arrive in order - the worst case for an unbalanced BST,
        // which would end up 1000 levels deep.
        var tree = new AvlTree<int, int>();
        for (var i = 1; i <= 1000; i++)
        {
            tree.Add(i, i);
        }

        Assert.True(tree.IsBalancedSearchTree());
        Assert.InRange(tree.Height, 10, 14); // AVL bound: < 1.44 * log2(n + 2)
        Assert.Equal(1000, tree.KeyCount);
    }

    [Fact]
    public void RandomInserts_InOrderIsSortedAndTreeIsBalanced()
    {
        var random = new Random(7);
        var keys = Enumerable.Range(0, 500).Select(_ => random.Next(10_000)).Distinct().ToList();
        var tree = new AvlTree<int, int>();
        foreach (var key in keys)
        {
            tree.Add(key, key * 2);
        }

        Assert.True(tree.IsBalancedSearchTree());
        Assert.Equal(keys.Order(), tree.InOrder().Select(e => e.Key));
        Assert.All(tree.InOrder(), e => Assert.Equal(e.Key * 2, e.Value));
    }

    [Fact]
    public void DuplicateKeys_AppendInsteadOfOverwriting()
    {
        var tree = new AvlTree<int, string>();
        tree.Add(5, "first");
        tree.Add(5, "second");
        tree.Add(4, "other");

        Assert.Equal(3, tree.Count);
        Assert.Equal(2, tree.KeyCount);
        Assert.Equal(["first", "second"], tree.Find(5));
    }

    [Fact]
    public void Find_MissingKey_ReturnsEmpty()
    {
        var tree = new AvlTree<int, string>();
        tree.Add(1, "one");

        Assert.Empty(tree.Find(2));
    }

    [Fact]
    public void Range_IsInclusiveAndOrdered()
    {
        var tree = new AvlTree<int, int>();
        foreach (var key in new[] { 50, 20, 80, 10, 30, 70, 90, 25, 35 })
        {
            tree.Add(key, key);
        }

        var result = tree.Range(25, 70).Select(e => e.Key);

        Assert.Equal([25, 30, 35, 50, 70], result);
    }

    [Fact]
    public void Range_WithNoMatches_ReturnsEmpty()
    {
        var tree = new AvlTree<int, int>();
        tree.Add(10, 10);
        tree.Add(20, 20);

        Assert.Empty(tree.Range(11, 19));
        Assert.Empty(tree.Range(21, 30));
    }

    [Fact]
    public void Range_OverTimestamps_ReturnsOnlyTheWindow()
    {
        var start = new DateTime(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc);
        var tree = new AvlTree<DateTime, double>();
        for (var minute = 0; minute < 60; minute++)
        {
            tree.Add(start.AddMinutes(minute), minute);
        }

        var window = tree.Range(start.AddMinutes(15), start.AddMinutes(19));

        Assert.Equal([15d, 16d, 17d, 18d, 19d], window.Select(e => e.Value));
    }

    [Fact]
    public void Range_StartAfterEnd_Throws()
    {
        var tree = new AvlTree<int, int>();

        Assert.Throws<ArgumentException>(() => tree.Range(5, 1));
    }

    [Fact]
    public void MinAndMaxKeys_AreTheExtremes()
    {
        var tree = new AvlTree<int, int>();
        Assert.False(tree.TryGetMinKey(out _));

        foreach (var key in new[] { 40, 10, 90, 60 })
        {
            tree.Add(key, key);
        }

        Assert.True(tree.TryGetMinKey(out var min));
        Assert.True(tree.TryGetMaxKey(out var max));
        Assert.Equal(10, min);
        Assert.Equal(90, max);
    }

    [Fact]
    public void Add_NullKey_Throws()
    {
        var tree = new AvlTree<string, int>();

        Assert.Throws<ArgumentNullException>(() => tree.Add(null!, 1));
    }
}
