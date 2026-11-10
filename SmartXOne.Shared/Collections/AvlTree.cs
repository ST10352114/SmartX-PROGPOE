namespace SmartXOne.Shared.Collections;

// Code Attribution
// Purpose: AVL insertion with height tracking and the LL / RR / LR / RL rebalancing rotations
// Author: GeeksforGeeks
// Title: Insertion in an AVL Tree
// Year: 2026
// URL/DOI: https://www.geeksforgeeks.org/dsa/insertion-in-an-avl-tree/
// Accessed: 10 November 2026

/// <summary>
/// A self-balancing binary search tree (AVL): after every insert the heights of
/// each node's two subtrees differ by at most one, so the tree's height stays
/// O(log n) no matter what order keys arrive in. Telemetry arrives in
/// timestamp order, which would turn a plain BST into a linked list - the
/// rotations here are what keep lookups and range queries logarithmic.
/// <para>
/// Each node holds a <em>list</em> of values, so two readings that share a key
/// (the same timestamp) are both kept rather than one overwriting the other.
/// </para>
/// <para>Not thread-safe: callers that share an instance must lock around it.</para>
/// </summary>
/// <remarks>
/// Complexity: <see cref="Add"/> and <see cref="Find"/> are O(log n);
/// <see cref="Range"/> is O(log n + k) for k results, because whole subtrees
/// that fall outside the range are skipped instead of scanned;
/// <see cref="InOrder"/> is O(n).
/// </remarks>
public sealed class AvlTree<TKey, TValue>
    where TKey : IComparable<TKey>
{
    private Node? _root;

    /// <summary>Total number of values stored (a key holding three values counts three).</summary>
    public int Count { get; private set; }

    /// <summary>Number of distinct keys (tree nodes).</summary>
    public int KeyCount { get; private set; }

    /// <summary>Height of the tree: 0 when empty, 1 for a single node.</summary>
    public int Height => HeightOf(_root);

    /// <summary>Adds <paramref name="value"/> under <paramref name="key"/>, appending if the key already exists, then rebalances.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The key type's <c>CompareTo</c> threw.</exception>
    public void Add(TKey key, TValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        _root = Insert(_root, key, value);
        Count++;
    }

    /// <summary>All values stored under exactly <paramref name="key"/>, in insertion order; empty if the key isn't present.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    public IReadOnlyList<TValue> Find(TKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var node = _root;
        while (node is not null)
        {
            var cmp = Compare(key, node.Key);
            if (cmp == 0)
            {
                return node.Values;
            }

            node = cmp < 0 ? node.Left : node.Right;
        }

        return [];
    }

    /// <summary>
    /// Every entry whose key lies in <c>[from, to]</c> (both inclusive), in
    /// ascending key order.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="from"/> or <paramref name="to"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="from"/> is after <paramref name="to"/>.</exception>
    public List<KeyValuePair<TKey, TValue>> Range(TKey from, TKey to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (Compare(from, to) > 0)
        {
            throw new ArgumentException("The start of the range must not be after the end.", nameof(from));
        }

        var results = new List<KeyValuePair<TKey, TValue>>();
        CollectRange(_root, from, to, results);
        return results;
    }

    /// <summary>The smallest key (the leftmost node) in O(log n); <c>false</c> when the tree is empty.</summary>
    public bool TryGetMinKey(out TKey key)
    {
        var node = _root;
        while (node?.Left is not null)
        {
            node = node.Left;
        }

        key = node is null ? default! : node.Key;
        return node is not null;
    }

    /// <summary>The largest key (the rightmost node) in O(log n); <c>false</c> when the tree is empty.</summary>
    public bool TryGetMaxKey(out TKey key)
    {
        var node = _root;
        while (node?.Right is not null)
        {
            node = node.Right;
        }

        key = node is null ? default! : node.Key;
        return node is not null;
    }

    /// <summary>Every entry in ascending key order. Iterative, so a very large tree can't overflow the call stack.</summary>
    public IEnumerable<KeyValuePair<TKey, TValue>> InOrder()
    {
        var stack = new Stack<Node>();
        var node = _root;

        while (node is not null || stack.Count > 0)
        {
            while (node is not null)
            {
                stack.Push(node);
                node = node.Left;
            }

            node = stack.Pop();
            foreach (var value in node.Values)
            {
                yield return new KeyValuePair<TKey, TValue>(node.Key, value);
            }

            node = node.Right;
        }
    }

    /// <summary>
    /// Verifies the BST ordering and AVL balance invariants over the whole
    /// tree. O(n); intended for tests, not for the hot path.
    /// </summary>
    public bool IsBalancedSearchTree() => Check(_root, out _);

    private Node Insert(Node? node, TKey key, TValue value)
    {
        if (node is null)
        {
            KeyCount++;
            return new Node(key, value);
        }

        var cmp = Compare(key, node.Key);
        if (cmp == 0)
        {
            node.Values.Add(value); // same key: keep both, structure unchanged
            return node;
        }

        if (cmp < 0)
        {
            node.Left = Insert(node.Left, key, value);
        }
        else
        {
            node.Right = Insert(node.Right, key, value);
        }

        UpdateHeight(node);
        return Rebalance(node);
    }

    private static Node Rebalance(Node node)
    {
        var balance = BalanceOf(node);

        if (balance > 1)
        {
            // Left-heavy. LR case: the left child leans right, so straighten it first.
            if (BalanceOf(node.Left!) < 0)
            {
                node.Left = RotateLeft(node.Left!);
            }

            return RotateRight(node); // LL case (or LR after the first rotation)
        }

        if (balance < -1)
        {
            // Right-heavy. RL case: the right child leans left, so straighten it first.
            if (BalanceOf(node.Right!) > 0)
            {
                node.Right = RotateRight(node.Right!);
            }

            return RotateLeft(node); // RR case (or RL after the first rotation)
        }

        return node;
    }

    private static Node RotateRight(Node y)
    {
        var x = y.Left!;
        y.Left = x.Right;
        x.Right = y;
        UpdateHeight(y);
        UpdateHeight(x);
        return x;
    }

    private static Node RotateLeft(Node x)
    {
        var y = x.Right!;
        x.Right = y.Left;
        y.Left = x;
        UpdateHeight(x);
        UpdateHeight(y);
        return y;
    }

    private void CollectRange(Node? node, TKey from, TKey to, List<KeyValuePair<TKey, TValue>> results)
    {
        if (node is null)
        {
            return;
        }

        var aboveFrom = Compare(node.Key, from) > 0;
        var belowTo = Compare(node.Key, to) < 0;

        // Only descend left if smaller keys could still be in range, and right
        // if larger ones could - this pruning is what makes the query O(log n + k).
        if (aboveFrom)
        {
            CollectRange(node.Left, from, to, results);
        }

        if (Compare(node.Key, from) >= 0 && Compare(node.Key, to) <= 0)
        {
            foreach (var value in node.Values)
            {
                results.Add(new KeyValuePair<TKey, TValue>(node.Key, value));
            }
        }

        if (belowTo)
        {
            CollectRange(node.Right, from, to, results);
        }
    }

    private bool Check(Node? node, out int height)
    {
        height = 0;
        if (node is null)
        {
            return true;
        }

        if (!Check(node.Left, out var leftHeight) || !Check(node.Right, out var rightHeight))
        {
            return false;
        }

        height = 1 + Math.Max(leftHeight, rightHeight);
        var ordered = (node.Left is null || Compare(node.Left.Key, node.Key) < 0)
                      && (node.Right is null || Compare(node.Right.Key, node.Key) > 0);

        return ordered && Math.Abs(leftHeight - rightHeight) <= 1 && node.Height == height;
    }

    private static int Compare(TKey a, TKey b)
    {
        try
        {
            return a.CompareTo(b);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Comparing keys '{a}' and '{b}' failed.", ex);
        }
    }

    private static int HeightOf(Node? node) => node?.Height ?? 0;

    private static int BalanceOf(Node node) => HeightOf(node.Left) - HeightOf(node.Right);

    private static void UpdateHeight(Node node) => node.Height = 1 + Math.Max(HeightOf(node.Left), HeightOf(node.Right));

    private sealed class Node(TKey key, TValue value)
    {
        public TKey Key { get; } = key;

        public List<TValue> Values { get; } = [value];

        public int Height { get; set; } = 1;

        public Node? Left { get; set; }

        public Node? Right { get; set; }
    }
}
