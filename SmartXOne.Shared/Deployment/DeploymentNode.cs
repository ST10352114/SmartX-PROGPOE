namespace SmartXOne.Shared.Deployment;

/// <summary>
/// One node in the nested device deployment hierarchy
/// (Facility &rarr; Zone &rarr; Sub-Zone &rarr; Node). A node carries an id, a
/// display name, its <see cref="DeploymentLevel"/>, and a list of child nodes.
/// A leaf (an actual sensor mounting point) simply has no children.
/// </summary>
public sealed class DeploymentNode
{
    /// <exception cref="ArgumentException"><paramref name="id"/> is null or blank.</exception>
    public DeploymentNode(string id, string name, DeploymentLevel level)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        Id = id;
        Name = name;
        Level = level;
    }

    /// <summary>Unique id of this node (this is the value the recursive validator searches for).</summary>
    public string Id { get; }

    /// <summary>Human-readable label, e.g. "Greenhouse 3" or "Row B drip line".</summary>
    public string Name { get; }

    /// <summary>Which tier of the hierarchy this node represents.</summary>
    public DeploymentLevel Level { get; }

    /// <summary>Child nodes one level down. Empty for a leaf.</summary>
    public List<DeploymentNode> Children { get; } = [];

    /// <summary>Fluent helper for building trees in code / tests / seed data.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="child"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="child"/> is this node itself (a one-node cycle).</exception>
    public DeploymentNode AddChild(DeploymentNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (ReferenceEquals(child, this))
        {
            throw new ArgumentException("A deployment node cannot be its own child.", nameof(child));
        }

        Children.Add(child);
        return this;
    }
}
