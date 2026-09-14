namespace SmartXOne.Shared.Deployment;

/// <summary>
/// (PoE requirement 3d &ndash; RECURSION) Walks a nested
/// <see cref="DeploymentNode"/> tree to confirm whether a given node id exists
/// somewhere inside it. Used when registering a sensor: the location the sensor
/// claims to be at must be a real node in the configured deployment.
/// </summary>
public static class DeploymentTreeValidator //validates the  tree of commands 
{
    /// <summary>
    /// Safety cap on recursion depth. A well-formed deployment is only four
    /// tiers deep; anything past this is treated as malformed rather than
    /// allowed to grow the call stack without bound.
    /// </summary>
    public const int MaxDepth = 64;

    /// <summary>
    /// Returns <c>true</c> if <paramref name="targetNodeId"/> matches
    /// <paramref name="root"/> or any node beneath it.
    /// </summary>
    /// <param name="root">Root of the tree (or subtree) to search. <c>null</c> yields <c>false</c>.</param>
    /// <param name="targetNodeId">The node id to look for.</param>
    /// <exception cref="ArgumentException"><paramref name="targetNodeId"/> is null or whitespace.</exception>
    /// <exception cref="InvalidOperationException">The tree is deeper than <see cref="MaxDepth"/> (malformed / cyclic input).</exception>
    public static bool ContainsNode(DeploymentNode? root, string targetNodeId)
    {
        if (string.IsNullOrWhiteSpace(targetNodeId))
        {
            throw new ArgumentException("Target node id must be provided.", nameof(targetNodeId));
        }

        // 'visited' guards against cycles: if the same node object is reached
        // twice (a mis-built tree that points back up), we stop instead of
        // recursing forever.
        var visited = new HashSet<DeploymentNode>(ReferenceEqualityComparer.Instance);
        return Search(root, targetNodeId, visited, depth: 0);
    }

    private static bool Search(DeploymentNode? node, string targetNodeId, HashSet<DeploymentNode> visited, int depth)
    {
        // --- Base cases ---------------------------------------------------
        // 1. Fell off the tree.
        if (node is null)
        {
            return false;
        }

        // 2. Cycle: this exact node was already examined on this walk.
        if (!visited.Add(node))
        {
            return false;
        }

        // 3. Depth cap: deeper than any valid deployment -> malformed input.
        if (depth >= MaxDepth)
        {
            throw new InvalidOperationException(
                $"Deployment tree exceeds the maximum supported depth of {MaxDepth}; input is malformed or cyclic.");
        }

        // 4. Match found.
        if (string.Equals(node.Id, targetNodeId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // --- Recursive step --------------------------------------------
        // Ask each child subtree the same question, one level deeper.
        foreach (var child in node.Children)
        {
            if (Search(child, targetNodeId, visited, depth + 1))
            {
                return true;
            }
        }

        return false;
    }
}
