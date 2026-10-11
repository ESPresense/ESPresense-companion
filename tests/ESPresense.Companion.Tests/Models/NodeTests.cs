using ESPresense.Models;

namespace ESPresense.Companion.Tests.Models;

[TestFixture]
public class NodeTests
{
    private static Floor MakeFloor(double zMin, double zMax, bool withBounds = true)
    {
        var floor = new Floor();
        var cf = new ConfigFloor { Id = "second", Name = "Second Floor" };
        if (withBounds) cf.Bounds = new[] { new double[] { 0, 0, zMin }, new double[] { 12, 12, zMax } };
        floor.Update(new Config(), cf);
        return floor;
    }

    private static Node MakeNode(double[]? point, Floor floor)
    {
        var node = new Node("test", NodeSourceType.Config);
        node.Update(new Config(), new ConfigNode { Name = "test", Point = point }, new[] { floor });
        return node;
    }

    [Test]
    public void ZOutOfBounds_TrueWhenFloorRelativeZUsedOnUpperFloor()
    {
        var node = MakeNode(new double[] { 1, 2, 0.5 }, MakeFloor(3, 6));
        Assert.That(node.ZOutOfBounds, Is.True);
    }

    [Test]
    public void ZOutOfBounds_FalseWhenZWithinFloorBounds()
    {
        var node = MakeNode(new double[] { 1, 2, 4.7 }, MakeFloor(3, 6));
        Assert.That(node.ZOutOfBounds, Is.False);
    }

    [Test]
    public void ZOutOfBounds_FalseWhenNoPointConfigured()
    {
        var node = MakeNode(null, MakeFloor(3, 6));
        Assert.That(node.ZOutOfBounds, Is.False);
    }

    [Test]
    public void ZOutOfBounds_FalseWhenFloorHasNoBounds()
    {
        var node = MakeNode(new double[] { 1, 2, 0.5 }, MakeFloor(0, 0, withBounds: false));
        Assert.That(node.ZOutOfBounds, Is.False);
    }
}
