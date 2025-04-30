using ESPresense.Locators;
using MathNet.Numerics.Optimization;
using MathNet.Spatial.Euclidean;
using Newtonsoft.Json;

namespace ESPresense.Models;

public class Scenario(Config? config, ILocate locator, string? name)
{
    private Config? Config { get; } = config;
    private ILocate Locator { get; } = locator;

    private readonly KalmanLocation _kalmanLocation = new();

    public bool Current => DateTime.UtcNow - LastHit < TimeSpan.FromSeconds(Config?.Timeout ?? 30);
    public int? Confidence { get; set; }

    /// <summary>
    /// Confidence value weighted by motion consistency with the predicted location
    /// </summary>
    public double WeightedConfidence { get; set; }

    public double? PearsonCorrelation { get; set; }

    public double? Minimum { get; set; }
    [JsonIgnore] public Point3D LastLocation { get; set; }
    public Point3D Location => _kalmanLocation.Location;
    public double? Scale { get; set; }
    public int? Fixes { get; set; }
    public string? Name { get; } = name;
    public Room? Room { get; set; }
    public double? Error { get; set; }
    public int? Iterations { get; set; }
    public ExitCondition ReasonForExit { get; set; }
    public Floor? Floor { get; set; }
    public DateTime? LastHit { get; set; }
    public double Probability { get; set; } = 1.0;

    public bool Locate()
    {
        return Locator.Locate(this);
    }

    public void UpdateLocation(Point3D newLocation)
    {
        LastHit = DateTime.UtcNow;
        LastLocation = Location;

        // Use the KalmanLocation to get a filtered position
        // Use confidence or default to 50 if not set
        _kalmanLocation.Update(newLocation, Confidence ?? 50);
    }
}
