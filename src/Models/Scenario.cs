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

    /// <summary>
    /// The unfiltered position from the latest <see cref="UpdateLocation"/>, before Kalman smoothing
    /// </summary>
    [JsonIgnore] public Point3D Fit { get; private set; }

    /// <summary>
    /// What the latest locator pass used; replaced whole so a reader never sees half a pass
    /// </summary>
    [JsonIgnore] public LocatorTrace? Trace { get; set; }

    public string LocatorName => Locator switch
    {
        NelderMeadMultilateralizer => "Nelder-Mead",
        BfgsMultilateralizer => "BFGS",
        MLEMultilateralizer => "MLE",
        GaussNewtonMultilateralizer => "Gauss-Newton",
        NadarayaWatsonMultilateralizer => "Nadaraya-Watson",
        MultiFloorMultilateralizer => "MultiFloor",
        NearestNode => "Nearest Node",
        AnchorLocator => "Anchored",
        _ => Locator.GetType().Name
    };

    internal bool IsNearestNode => Locator is NearestNode;

    /// <summary>
    /// Locator and floor together: <see cref="Name"/> alone is the floor, shared by every locator on
    /// it. Name stays as it is because it's published as best_scenario and stored in history.
    /// </summary>
    public string Label => Locator is BaseMultilateralizer or NadarayaWatsonMultilateralizer
        ? $"{LocatorName} ({Name})"
        : LocatorName;

    public bool Locate()
    {
        return Locator.Locate(this);
    }

    public void UpdateLocation(Point3D newLocation)
    {
        LastHit = DateTime.UtcNow;
        LastLocation = Location;
        Fit = newLocation;

        // Use the KalmanLocation to get a filtered position
        _kalmanLocation.Update(newLocation);
    }

    public void ResetLocation(Point3D newLocation)
    {
        LastHit = DateTime.UtcNow;
        LastLocation = newLocation;
        _kalmanLocation.Reset(newLocation);
    }
}
