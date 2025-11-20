using ESPresense.Models;
using ESPresense.Utils;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Locators;

public class AnchorLocator : ILocate
{
    private readonly Point3D _anchorLocation;
    private readonly Floor? _floor;
    private readonly Room? _room;
    private readonly IEnumerable<Floor>? _floors;

    public AnchorLocator(Point3D anchorLocation, Floor? floor = null, Room? room = null, IEnumerable<Floor>? floors = null)
    {
        _anchorLocation = anchorLocation;
        _floor = floor;
        _room = room;
        _floors = floors;
    }

    public bool Locate(Scenario scenario)
    {
        const double Epsilon = 0.01;

        var previousLocation = scenario.Location;
        var hasPrevious = scenario.LastHit.HasValue;
        var moved = hasPrevious && previousLocation.DistanceTo(_anchorLocation) > Epsilon;

        if (!hasPrevious)
        {
            moved = true;
        }

        // Set anchor properties
        scenario.Scale = 1.0;
        scenario.Error = 0.0;
        scenario.Iterations = 0;
        scenario.ResetLocation(_anchorLocation);
        scenario.Confidence = 100;

        // Prefer explicit floor/room if provided, otherwise try to find them spatially
        if (_floor != null || _room != null)
        {
            scenario.Floor = _floor;
            scenario.Room = _room;
        }
        else if (_floors != null)
        {
            var (floor, room) = SpatialUtils.FindFloorAndRoom(_anchorLocation, _floors);
            scenario.Floor = floor;
            scenario.Room = room;
        }

        return moved;
    }
}
