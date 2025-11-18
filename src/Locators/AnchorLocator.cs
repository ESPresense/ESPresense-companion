using ESPresense.Models;
using ESPresense.Utils;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Locators;

public class AnchorLocator : ILocate
{
    private readonly Point3D _anchorLocation;
    private readonly IEnumerable<Floor>? _floors;

    public AnchorLocator(Point3D anchorLocation, IEnumerable<Floor>? floors = null)
    {
        _anchorLocation = anchorLocation;
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

        // Determine floor and room based on anchor location for consistency
        if (_floors != null)
        {
            var (floor, room) = SpatialUtils.FindFloorAndRoom(_anchorLocation, _floors);
            scenario.Floor = floor;
            scenario.Room = room;
        }

        return moved;
    }
}
