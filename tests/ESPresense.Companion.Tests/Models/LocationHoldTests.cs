using ESPresense.Models;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Companion.Tests.Models;

[TestFixture]
public class LocationHoldTests
{
    private static readonly DateTime T0 = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
    private static readonly ConfigFiltering Defaults = new() { RoomHold = 3, RoomMargin = 0.25, FloorHold = 5 };

    private Floor _ground = null!, _upper = null!;
    private Room _kitchen = null!, _hall = null!, _bedroom = null!, _landing = null!;

    [SetUp]
    public void SetUp()
    {
        _ground = MakeFloor("ground", 0, 3, Square("Kitchen", 0, 0, 4, 4), Square("Hall", 4, 0, 8, 4));
        _upper = MakeFloor("upper", 3, 6, Square("Bedroom", 0, 0, 4, 4), Square("Landing", 4, 0, 8, 4));
        _kitchen = _ground.Rooms.Values.Single(r => r.Name == "Kitchen");
        _hall = _ground.Rooms.Values.Single(r => r.Name == "Hall");
        _bedroom = _upper.Rooms.Values.Single(r => r.Name == "Bedroom");
        _landing = _upper.Rooms.Values.Single(r => r.Name == "Landing");
    }

    private static Floor MakeFloor(string id, double z0, double z1, params ConfigRoom[] rooms)
    {
        var floor = new Floor();
        floor.Update(new Config(), new ConfigFloor { Id = id, Name = id, Bounds = [[0, 0, z0], [8, 4, z1]], Rooms = rooms });
        return floor;
    }

    private static ConfigRoom Square(string name, double x0, double y0, double x1, double y1) =>
        new() { Name = name, Points = [[x0, y0], [x1, y0], [x1, y1], [x0, y1]] };

    private static DateTime At(double seconds) => T0.AddSeconds(seconds);

    [Test]
    public void FirstUpdate_IsTakenAsItIs()
    {
        var hold = new LocationHold();
        Assert.That(hold.Update(_ground, _kitchen, new Point3D(2, 2, 1), T0, Defaults), Is.True);
        Assert.That(hold.Room, Is.SameAs(_kitchen));
        Assert.That(hold.Floor, Is.SameAs(_ground));
    }

    [Test]
    public void ShortExcursionIntoTheNextRoom_KeepsTheRoom()
    {
        var hold = new LocationHold();
        hold.Update(_ground, _kitchen, new Point3D(2, 2, 1), At(0), Defaults);

        // 2 s in the hall, well past the margin, then back
        hold.Update(_ground, _hall, new Point3D(6, 2, 1), At(1), Defaults);
        hold.Update(_ground, _hall, new Point3D(6, 2, 1), At(3), Defaults);
        Assert.That(hold.Room, Is.SameAs(_kitchen));
        Assert.That(hold.Challenger, Is.EqualTo("Hall"));

        hold.Update(_ground, _kitchen, new Point3D(2, 2, 1), At(3.5), Defaults);
        Assert.That(hold.Room, Is.SameAs(_kitchen));
        Assert.That(hold.Challenger, Is.Null);

        // The excursion's time doesn't count towards the next one
        hold.Update(_ground, _hall, new Point3D(6, 2, 1), At(4), Defaults);
        Assert.That(hold.Update(_ground, _hall, new Point3D(6, 2, 1), At(6), Defaults), Is.False);
        Assert.That(hold.Room, Is.SameAs(_kitchen));
    }

    [Test]
    public void ARoomThatHolds_TakesOver()
    {
        var hold = new LocationHold();
        hold.Update(_ground, _kitchen, new Point3D(2, 2, 1), At(0), Defaults);
        hold.Update(_ground, _hall, new Point3D(6, 2, 1), At(1), Defaults);
        Assert.That(hold.Update(_ground, _hall, new Point3D(6, 2, 1), At(4), Defaults), Is.True);
        Assert.That(hold.Room, Is.SameAs(_hall));
        Assert.That(hold.Challenger, Is.Null);
    }

    [Test]
    public void AFixJustOutsideTheWall_KeepsTheRoomForever()
    {
        var hold = new LocationHold();
        hold.Update(_ground, _kitchen, new Point3D(2, 2, 1), At(0), Defaults);

        // 0.2 m into the hall: inside the 0.25 m margin
        for (var t = 1; t < 60; t++)
            hold.Update(_ground, _hall, new Point3D(4.2, 2, 1), At(t), Defaults);

        Assert.That(hold.Room, Is.SameAs(_kitchen));
        Assert.That(hold.Challenger, Is.Null);
    }

    [Test]
    public void AFixPastTheMargin_ChallengesAndTakesOverAfterTheHold()
    {
        var hold = new LocationHold();
        hold.Update(_ground, _kitchen, new Point3D(2, 2, 1), At(0), Defaults);

        // 0.4 m into the hall: past the 0.25 m margin, so the hall challenges
        hold.Update(_ground, _hall, new Point3D(4.4, 2, 1), At(1), Defaults);
        Assert.That(hold.Room, Is.SameAs(_kitchen));
        Assert.That(hold.Challenger, Is.EqualTo("Hall"));

        hold.Update(_ground, _hall, new Point3D(4.4, 2, 1), At(4), Defaults);
        Assert.That(hold.Room, Is.SameAs(_hall));
    }

    [Test]
    public void DefaultMargin_IsAQuarterMetre()
    {
        Assert.That(new ConfigFiltering().RoomMargin, Is.EqualTo(0.25));
    }

    [Test]
    public void OutsideEveryRoom_IsAChallengerLikeAnyOther()
    {
        var hold = new LocationHold();
        hold.Update(_ground, _kitchen, new Point3D(2, 2, 1), At(0), Defaults);
        hold.Update(_ground, null, new Point3D(2, 6, 1), At(1), Defaults);
        Assert.That(hold.Room, Is.SameAs(_kitchen));
        Assert.That(hold.Challenger, Is.EqualTo("no room"));

        hold.Update(_ground, null, new Point3D(2, 6, 1), At(4), Defaults);
        Assert.That(hold.Room, Is.Null);
        Assert.That(hold.Floor, Is.SameAs(_ground));
    }

    [Test]
    public void AFloorChange_NeedsTheFloorHold_AndKeepsTheRoomMeanwhile()
    {
        var hold = new LocationHold();
        hold.Update(_ground, _kitchen, new Point3D(2, 2, 1), At(0), Defaults);

        hold.Update(_upper, _bedroom, new Point3D(2, 2, 4), At(1), Defaults);
        // The room on the new floor flickers; the floor challenge keeps counting
        hold.Update(_upper, _landing, new Point3D(5, 2, 4), At(3), Defaults);
        hold.Update(_upper, _bedroom, new Point3D(2, 2, 4), At(5), Defaults);
        Assert.That(hold.Floor, Is.SameAs(_ground));
        Assert.That(hold.Room, Is.SameAs(_kitchen));
        Assert.That(hold.Challenger, Is.EqualTo("upper"));

        Assert.That(hold.Update(_upper, _landing, new Point3D(5, 2, 4), At(6), Defaults), Is.True);
        Assert.That(hold.Floor, Is.SameAs(_upper));
        Assert.That(hold.Room, Is.SameAs(_landing), "the room is taken as it is when the floor switches");
    }

    [Test]
    public void ABriefFloorFlip_IsIgnored()
    {
        var hold = new LocationHold();
        hold.Update(_ground, _kitchen, new Point3D(2, 2, 1), At(0), Defaults);
        hold.Update(_upper, _bedroom, new Point3D(2, 2, 4), At(1), Defaults);
        hold.Update(_upper, _bedroom, new Point3D(2, 2, 4), At(4), Defaults);
        hold.Update(_ground, _kitchen, new Point3D(2, 2, 1), At(4.5), Defaults);
        hold.Update(_upper, _bedroom, new Point3D(2, 2, 4), At(5), Defaults);
        hold.Update(_upper, _bedroom, new Point3D(2, 2, 4), At(9), Defaults);
        Assert.That(hold.Floor, Is.SameAs(_ground));
    }

    [Test]
    public void ZeroHolds_SwitchAtOnce()
    {
        var off = new ConfigFiltering { RoomHold = 0, RoomMargin = 0, FloorHold = 0 };
        var hold = new LocationHold();
        hold.Update(_ground, _kitchen, new Point3D(2, 2, 1), At(0), off);
        Assert.That(hold.Update(_ground, _hall, new Point3D(4.1, 2, 1), At(0.1), off), Is.True);
        Assert.That(hold.Room, Is.SameAs(_hall));
        Assert.That(hold.Update(_upper, _bedroom, new Point3D(2, 2, 4), At(0.2), off), Is.True);
        Assert.That(hold.Floor, Is.SameAs(_upper));
    }

    [Test]
    public void Reset_TakesTheNextUpdateAsItIs()
    {
        var hold = new LocationHold();
        hold.Update(_ground, _kitchen, new Point3D(2, 2, 1), At(0), Defaults);
        hold.Reset();
        Assert.That(hold.IsHeld, Is.False);
        Assert.That(hold.Update(_upper, _landing, new Point3D(5, 2, 4), At(1), Defaults), Is.True);
        Assert.That(hold.Room, Is.SameAs(_landing));
    }
}
