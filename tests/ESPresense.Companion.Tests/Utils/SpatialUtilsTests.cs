using ESPresense.Models;
using ESPresense.Utils;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Companion.Tests.Utils;

[TestFixture]
public class SpatialUtilsTests
{
    // Two 4 m wide rooms either side of a 1 m gap: west spans x 0-4, east spans x 5-9.
    private static readonly Room West = MakeRoom("west", 0, 4);
    private static readonly Room East = MakeRoom("east", 5, 9);
    private static readonly Room[] Rooms = { West, East };

    private static Room MakeRoom(string id, double minX, double maxX)
    {
        var room = new Room();
        room.Update(new Config(), new Floor(), new ConfigRoom
        {
            Id = id,
            Name = id,
            Points = new[]
            {
                new[] { minX, 0.0 }, new[] { maxX, 0.0 }, new[] { maxX, 4.0 }, new[] { minX, 4.0 }
            }
        });
        return room;
    }

    [Test]
    public void ContainingRoom_WinsOverACloserEdgeOfAnotherRoom()
    {
        Assert.That(SpatialUtils.FindRoomNearest(new Point3D(3.99, 2, 1), Rooms, 1.0), Is.SameAs(West));
    }

    [Test]
    public void FixInTheGap_GetsTheNearestRoom()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SpatialUtils.FindRoomNearest(new Point3D(4.3, 2, 1), Rooms, 1.0), Is.SameAs(West));
            Assert.That(SpatialUtils.FindRoomNearest(new Point3D(4.7, 2, 1), Rooms, 1.0), Is.SameAs(East));
        });
    }

    [Test]
    public void FixBeyondTheLimit_GetsNoRoom()
    {
        Assert.That(SpatialUtils.FindRoomNearest(new Point3D(-1.5, 2, 1), Rooms, 1.0), Is.Null);
    }

    [Test]
    public void DistanceIsMeasuredToTheCorner_NotAlongOneAxis()
    {
        // 0.8 m past the corner on each axis: within 1 m of either wall's line, 1.13 m from the room.
        Assert.That(SpatialUtils.FindRoomNearest(new Point3D(-0.8, -0.8, 1), Rooms, 1.0), Is.Null);
    }

    [Test]
    public void ZeroDistance_TurnsTheFallbackOff()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SpatialUtils.FindRoomNearest(new Point3D(4.3, 2, 1), Rooms, 0), Is.Null);
            Assert.That(SpatialUtils.FindRoomNearest(new Point3D(2, 2, 1), Rooms, 0), Is.SameAs(West));
        });
    }

    [Test]
    public void NaNFix_GetsNoRoom()
    {
        Assert.That(SpatialUtils.FindRoomNearest(Point3D.NaN, Rooms, 1.0), Is.Null);
    }

    [Test]
    public void RepeatedVertex_DoesNotThrow()
    {
        var room = new Room();
        room.Update(new Config(), new Floor(), new ConfigRoom
        {
            Id = "repeat",
            Name = "repeat",
            Points = new[] { new[] { 0.0, 0.0 }, new[] { 4.0, 0.0 }, new[] { 4.0, 0.0 }, new[] { 4.0, 4.0 }, new[] { 0.0, 4.0 } }
        });

        Assert.That(SpatialUtils.FindRoomNearest(new Point3D(4.5, 2, 1), new[] { room }, 1.0), Is.SameAs(room));
    }

    [Test]
    public void ClosedPolygon_StillWorks()
    {
        var room = new Room();
        room.Update(new Config(), new Floor(), new ConfigRoom
        {
            Id = "closed",
            Name = "closed",
            Points = new[] { new[] { 0.0, 0.0 }, new[] { 4.0, 0.0 }, new[] { 4.0, 4.0 }, new[] { 0.0, 0.0 } }
        });

        Assert.That(SpatialUtils.FindRoomNearest(new Point3D(4.5, 2, 1), new[] { room }, 1.0), Is.SameAs(room));
    }
}
