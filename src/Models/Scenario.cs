using ESPresense.Locators;
using MathNet.Numerics.Optimization;
using MathNet.Spatial.Euclidean;
using System.Text.Json.Serialization;

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

    /// <summary>
    /// After this many abandoned solves in a row the next one runs to the end, so a device heard by
    /// many nodes still gets a position when its readings arrive faster than a solve finishes.
    /// </summary>
    internal const int MaxConsecutiveAbandons = 3;

    private Device? _solveDevice;
    private long _solveVersion;
    private int _abandoned;

    /// <summary>Solves dropped in a row because a newer reading superseded them.</summary>
    internal int ConsecutiveAbandons => _abandoned;

    public bool Locate()
    {
        try
        {
            var moved = Locator.Locate(this);
            _abandoned = 0;
            return moved;
        }
        catch (ReadingsSupersededException)
        {
            // A newer reading re-queued the device, and the next pass solves on it; this result would be stale.
            _abandoned++;
            return false;
        }
        finally
        {
            _solveDevice = null;
        }
    }

    /// <summary>
    /// Takes the device's current readings for one solve and notes their version, so
    /// <see cref="ThrowIfSuperseded"/> can stop the solve once a newer reading arrives.
    /// </summary>
    internal DeviceToNode[] TakeReadings(Device device)
    {
        _solveDevice = device;
        _solveVersion = device.ReadingVersion; // before the copy: a reading landing during it bumps past this
        return device.CurrentReadings();
    }

    /// <summary>Called from a solver's objective: stops a solve whose readings have been superseded.</summary>
    internal void ThrowIfSuperseded()
    {
        if (_solveDevice != null && _abandoned < MaxConsecutiveAbandons && _solveDevice.ReadingVersion != _solveVersion)
            throw new ReadingsSupersededException();
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
