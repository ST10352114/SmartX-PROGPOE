using SmartXOne.Shared.Deployment;

namespace SmartXOne.Api.Sensors;

/// <summary>
/// The gateway's configured deployment hierarchy (Facility -&gt; Zone -&gt;
/// Sub-Zone -&gt; Node). Sensor registration checks its <c>DeploymentNodeId</c>
/// against this tree via <see cref="DeploymentTreeValidator.ContainsNode"/> -
/// the same recursive walk demonstrated in the Phase 3 shared-feature demo,
/// now doing real validation work instead of a throwaway example.
/// </summary>
public static class DeploymentSeed
{
    public static DeploymentNode BuildRoot()
    {
        var facility = new DeploymentNode("FAC-1", "Riverside Hydroponics", DeploymentLevel.Facility);

        var greenhouseA = new DeploymentNode("ZONE-A", "Greenhouse A", DeploymentLevel.Zone);
        var rowA3 = new DeploymentNode("SZ-A3", "Row 3 drip line", DeploymentLevel.SubZone);
        rowA3.AddChild(new DeploymentNode("NODE-A3-07", "Mounting point 7", DeploymentLevel.Node));
        rowA3.AddChild(new DeploymentNode("NODE-A3-08", "Mounting point 8", DeploymentLevel.Node));
        greenhouseA.AddChild(rowA3);

        var greenhouseB = new DeploymentNode("ZONE-B", "Greenhouse B", DeploymentLevel.Zone);
        var plantRoomB = new DeploymentNode("SZ-B1", "Plant room 1", DeploymentLevel.SubZone);
        plantRoomB.AddChild(new DeploymentNode("NODE-B1-01", "Mounting point 1", DeploymentLevel.Node));
        greenhouseB.AddChild(plantRoomB);

        facility.AddChild(greenhouseA);
        facility.AddChild(greenhouseB);

        return facility;
    }
}
