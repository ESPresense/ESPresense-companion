using ESPresense.Companion.Utils;
using ESPresense.Extensions;
using ESPresense.Models;
using MathNet.Spatial.Euclidean;
using Serilog;

namespace ESPresense.Locators;

public class NadarayaWatsonMultilateralizer(Device device, Floor floor, State state) : ILocate
{
    public bool Locate(Scenario scenario)
    {
        var confidence = scenario.Confidence;

        var nodes = device.Nodes.Values
            .Where(a => a.Current && (a.Node?.Floors?.Contains(floor) ?? false))
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

        scenario.Floor = floor;

        var guess = confidence < 5
            ? Point3D.MidPoint(positions[0], positions[1])
            : scenario.Location;

        try
        {
            if (positions.Length < 3 || floor.Bounds == null)
            {
                confidence = 1;
                scenario.UpdateLocation(guess);
            }
            else
            {
                // Nadaraya-Watson estimator implementation
                double epsilon = 1e-6;

                var weights = nodes.Select(dn =>
                {
                    return 1.0 / (Math.Pow(dn.Distance, 2) + epsilon);
                }).ToArray();

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
            Log.Error("Error finding location for {0}: {1}", device, ex.Message);
        }

        scenario.Confidence = confidence;

        if (nodes.Length >= 2)
        {
            var measuredDistances = nodes.Select(dn => dn.Distance).ToList();
            var calculatedDistances = nodes.Select(dn => scenario.Location.DistanceTo(dn.Node!.Location)).ToList();
            scenario.PearsonCorrelation = MathUtils.CalculatePearsonCorrelation(measuredDistances, calculatedDistances);
        }
        else
        {
            scenario.PearsonCorrelation = null; // Not enough data points
        }

        if (confidence <= 0) return false;
        if (Math.Abs(scenario.Location.DistanceTo(scenario.LastLocation)) < 0.1) return false;

        scenario.Room = floor.Rooms.Values.FirstOrDefault(a =>
            a.Polygon?.EnclosesPoint(scenario.Location.ToPoint2D()) ?? false);

        return true;
    }
}
