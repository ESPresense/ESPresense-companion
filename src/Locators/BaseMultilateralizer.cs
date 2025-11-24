using ESPresense.Extensions;
using ESPresense.Models;
using ESPresense.Utils;
using MathNet.Spatial.Euclidean;
using Serilog;

namespace ESPresense.Locators;

/// <summary>
/// Base class for multilateralizer implementations providing common functionality
/// </summary>
public abstract class BaseMultilateralizer : ILocate
{
    protected readonly Device Device;
    protected readonly Floor Floor;
    protected readonly State State;

    protected BaseMultilateralizer(Device device, Floor floor, State state)
    {
        Device = device;
        Floor = floor;
        State = state;
    }

    public abstract bool Locate(Scenario scenario);

    /// <summary>
    /// Initializes scenario with valid nodes and performs initial validation
    /// </summary>
    /// <returns>True if there are enough nodes to proceed with localization</returns>
    protected bool InitializeScenario(Scenario scenario, out DeviceToNode[] nodes, out Point3D guess)
    {
        var confidence = scenario.Confidence;

        nodes = Device.Nodes.Values
            .Where(a => a.Current && (a.Node?.Floors?.Contains(Floor) ?? false))
            .OrderBy(a => a.Distance)
            .ToArray();

        var pos = nodes.Select(a => a.Node!.Location).ToArray();

        scenario.Minimum = nodes.Min(a => (double?)a.Distance);
        scenario.LastHit = nodes.Max(a => a.LastHit);
        scenario.Fixes = pos.Length;

        if (pos.Length <= 1)
        {
            ResetScenario(scenario);
            guess = Point3D.NaN;
            return false;
        }

        scenario.Floor = Floor;

        guess = confidence < 5
            ? Point3D.MidPoint(pos[0], pos[1])
            : scenario.Location;

        return true;
    }

    /// <summary>
    /// Resets scenario properties when localization cannot proceed
    /// </summary>
    protected void ResetScenario(Scenario scenario)
    {
        scenario.Room = null;
        scenario.Confidence = 0;
        scenario.Error = null;
        scenario.Floor = null;
    }

    /// <summary>
    /// Clamps a point to within floor bounds
    /// </summary>
    protected Point3D ClampToFloorBounds(Point3D point)
    {
        if (Floor.Bounds == null || Floor.Bounds.Length < 2)
            return point;

        return new Point3D(
            Math.Clamp(point.X, Floor.Bounds[0].X, Floor.Bounds[1].X),
            Math.Clamp(point.Y, Floor.Bounds[0].Y, Floor.Bounds[1].Y),
            Math.Clamp(point.Z, Floor.Bounds[0].Z, Floor.Bounds[1].Z)
        );
    }

    /// <summary>
    /// Calculates and sets Pearson correlation for the scenario
    /// </summary>
    protected void CalculateAndSetPearsonCorrelation(Scenario scenario, DeviceToNode[] nodes)
    {
        if (nodes.Length >= 2)
        {
            var measuredDistances = nodes.Select(dn => dn.Distance).ToList();
            var calculatedDistances = nodes.Select(dn => scenario.Location.DistanceTo(dn.Node!.Location)).ToList();
            scenario.PearsonCorrelation = MathUtils.CalculatePearsonCorrelation(measuredDistances, calculatedDistances);
        }
        else
        {
            scenario.PearsonCorrelation = null;
        }
    }

    /// <summary>
    /// Assigns room to scenario based on location
    /// </summary>
    protected void AssignRoom(Scenario scenario)
    {
        scenario.Room = Floor.Rooms.Values.FirstOrDefault(a =>
            a.Polygon?.EnclosesPoint(scenario.Location.ToPoint2D()) ?? false);
    }

    /// <summary>
    /// Performs final validation and room assignment
    /// </summary>
    /// <returns>True if scenario has moved and is valid</returns>
    protected bool FinalizeScenario(Scenario scenario, int confidence)
    {
        scenario.Confidence = confidence;

        if (confidence <= 0) return false;
        if (Math.Abs(scenario.Location.DistanceTo(scenario.LastLocation)) < 0.1) return false;

        AssignRoom(scenario);
        return true;
    }

    /// <summary>
    /// Handles exceptions during localization
    /// </summary>
    protected int HandleLocatorException(Exception ex, Scenario scenario, Point3D fallbackGuess)
    {
        scenario.UpdateLocation(!double.IsNaN(fallbackGuess.X) ? fallbackGuess : new Point3D());
        Log.Error("Error finding location for {Device}: {Message}", Device, ex.Message);
        return 0;
    }
}
