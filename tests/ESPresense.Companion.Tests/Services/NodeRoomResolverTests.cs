using ESPresense.Models;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using NUnit.Framework;

namespace ESPresense.Companion.Tests.Services;

[TestFixture]
public class NodeRoomResolverTests
{
    private static Config _config = new();

    private static Floor BuildFloor(string id, double[] min, double[] max, params ConfigRoom[] rooms)
    {
        var floor = new Floor();
        floor.Update(_config, new ConfigFloor { Id = id, Name = id, Bounds = new[] { min, max }, Rooms = rooms });
        return floor;
    }

    private static Node BuildNode(string id, Point3D location, params Floor[] floors)
    {
        var node = new Node(id, NodeSourceType.Config);
        node.Update(_config, new ConfigNode { Id = id, Name = id, Point = new[] { location.X, location.Y, location.Z } }, floors);
        return node;
    }

    private static ConfigRoom Square(string name, double x0, double y0, double x1, double y1) => new()
    {
        Name = name,
        Points = new[] { new[] { x0, y0 }, new[] { x1, y0 }, new[] { x1, y1 }, new[] { x0, y1 } }
    };

    [Test]
    public void Resolve_NodeInsideARoomPolygon_ReturnsThatRoom()
    {
        var floor = BuildFloor("first", new[] { 0.0, 0, 0 }, new[] { 10.0, 10, 3 }, Square("Laundry", 0, 0, 4, 4));
        var node = BuildNode("laundry-node", new Point3D(2, 2, 1), floor);

        var room = NodeRoomResolver.Resolve(node);

        Assert.That(room?.Name, Is.EqualTo("Laundry"));
    }

    [Test]
    public void Resolve_NodeOutsideEveryRoomPolygon_ReturnsNull()
    {
        var floor = BuildFloor("first", new[] { 0.0, 0, 0 }, new[] { 10.0, 10, 3 }, Square("Laundry", 0, 0, 4, 4));
        var node = BuildNode("hallway-node", new Point3D(8, 8, 1), floor);

        Assert.That(NodeRoomResolver.Resolve(node), Is.Null);
    }

    [Test]
    public void Resolve_NodeOutsideFloorZRange_IsSkippedEvenIfXyMatchesARoom()
    {
        // Floor's z-bounds don't cover the node's z, even though its x/y sits inside a room polygon.
        var floor = BuildFloor("second", new[] { 0.0, 0, 3 }, new[] { 10.0, 10, 6 }, Square("Office", 0, 0, 4, 4));
        var node = BuildNode("wrong-floor-node", new Point3D(2, 2, 1), floor);

        Assert.That(NodeRoomResolver.Resolve(node), Is.Null);
    }

    [Test]
    public void Resolve_NodeWithNoLocation_ReturnsNull()
    {
        var node = new Node("no-location", NodeSourceType.Config);
        Assert.That(NodeRoomResolver.Resolve(node), Is.Null);
    }

    [Test]
    public void Resolve_ChecksEachCandidateFloor_UntilOneContainsTheRoom()
    {
        var floorA = BuildFloor("a", new[] { 0.0, 0, 0 }, new[] { 10.0, 10, 3 }, Square("Family", 0, 0, 4, 4));
        var floorB = BuildFloor("b", new[] { 0.0, 0, 3 }, new[] { 10.0, 10, 6 }, Square("Office", 0, 0, 4, 4));
        // A node visible from both floors but physically on floor b's z range and inside Office.
        var node = BuildNode("dual-floor-node", new Point3D(2, 2, 4), floorA, floorB);

        var room = NodeRoomResolver.Resolve(node);

        Assert.That(room?.Name, Is.EqualTo("Office"));
    }
}
