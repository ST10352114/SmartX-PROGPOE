namespace SmartXOne.Shared.Deployment;

/// <summary>
/// The tier a <see cref="DeploymentNode"/> sits at in the device deployment
/// hierarchy: Facility &rarr; Zone &rarr; Sub-Zone &rarr; Node.
/// </summary>
public enum DeploymentLevel
{
    Facility = 0,
    Zone = 1,
    SubZone = 2,
    Node = 3,
}
//nodes are seeded