using ESPresense.Extensions;
using ESPresense.Models;
using ESPresense.Weighting;
using MathNet.Spatial.Euclidean;
using Serilog;

namespace ESPresense.Locators;

public class NadarayaWatsonMultilateralizer : ILocate
{
    private readonly Device _device;
    private readonly Floor _floor;
    private readonly State _state;
    private readonly IKernel _kernel;

    public NadarayaWatsonMultilateralizer(Device device, Floor floor, State state, ConfigKernel? kernelConfig = null)
    {
        _device = device;
        _floor = floor;
        _state = state;

        _kernel = kernelConfig?.Algorithm?.ToLower() switch
        {
            "epanechnikov" => new EpanechnikovKernel(kernelConfig?.Props),
            "gaussian" => new GaussianKernel(kernelConfig?.Props),
            _ => new InverseSquareKernel(kernelConfig?.Props)
        };
    }

    public bool Locate(Scenario scenario)
    {
        var confidence = scenario.Confidence;

        var nodes = _device.Nodes.Values
            .Where(a => a.Current && (a.Node?.Floors?.Contains(_floor) ?? false))
            .OrderBy(a => a.Distance)
            .ToArray();

        var positions = nodes.Select(a => a.Node!.Location).ToArray();

        scenario.Minimum = nodes.Min(a => (double?)a.Distance);
        scenario.LastHit = nodes.Max(a => a.LastHit);
        scenario.Fixes = positions.Length;

        if (positions.Length <= 1)
        {
            scenario.Room = null;
            scenario.Confidence = 0;
            scenario.Error = null;
            scenario.Floor = null;
            return false;
        }

        scenario.Floor = _floor;

        var guess = confidence < 5
            ? Point3D.MidPoint(positions[0], positions[1])
            : scenario.Location;

        try
        {
            if (positions.Length < 3 || _floor.Bounds == null)
            {
                confidence = 1;
                scenario.UpdateLocation(guess);
            }
            else
            {

                var weights = nodes.Select(dn => _kernel.Evaluate(dn.Distance)).ToArray();
                var totalWeight = weights.Sum();

                var weightedX = nodes.Zip(weights, (dn, w) => dn.Node!.Location.X * w).Sum();
                var weightedY = nodes.Zip(weights, (dn, w) => dn.Node!.Location.Y * w).Sum();
                var weightedZ = nodes.Zip(weights, (dn, w) => dn.Node!.Location.Z * w).Sum();

                var estimatedLocation = new Point3D(
                    weightedX / totalWeight,
                    weightedY / totalWeight,
                    weightedZ / totalWeight
                );

                scenario.UpdateLocation(estimatedLocation);

                // Calculate weighted error
                var weightedError = nodes.Zip(weights, (dn, w) =>
                {
                    var estimatedDistance = estimatedLocation.DistanceTo(dn.Node!.Location);
                    var residual = estimatedDistance - dn.Distance;
                    return w * Math.Pow(residual, 2);
                }).Sum() / totalWeight;

                scenario.Error = weightedError;
                scenario.Iterations = null;
                //scenario.ReasonForExit = ;

                confidence = (int)Math.Min(100, Math.Max(10, 100.0 - (weightedError * 10)));
            }
        }
        catch (Exception ex)
        {
            confidence = 0;
            scenario.UpdateLocation(new Point3D());
            Log.Error("Error finding location for {0}: {1}", _device, ex.Message);
        }

        scenario.Confidence = confidence;

        if (confidence <= 0) return false;
        if (Math.Abs(scenario.Location.DistanceTo(scenario.LastLocation)) < 0.1) return false;

        scenario.Room = _floor.Rooms.Values.FirstOrDefault(a =>
            a.Polygon?.EnclosesPoint(scenario.Location.ToPoint2D()) ?? false);

        return true;
    }
}
