using ESPresense.Models;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Locators;

public class AnchorLocator : ILocate
{
    private readonly Point3D _anchorLocation;

    public AnchorLocator(Point3D anchorLocation)
    {
        _anchorLocation = anchorLocation;
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

        return moved;
    }
}
