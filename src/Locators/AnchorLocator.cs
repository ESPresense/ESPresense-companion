using ESPresense.Models;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Locators;

public class AnchorLocator(Point3D anchor) : ILocate
{
    private readonly Point3D _anchor = anchor;
    public bool Locate(Scenario scenario)
    {
        var moved = scenario.Location.DistanceTo(_anchor) > 0.01;
        scenario.Scale = 1;
        scenario.Error = 0;
        scenario.Iterations = 0;
        scenario.UpdateLocation(_anchor);
        scenario.Confidence = 100;
        return moved;
    }
}
