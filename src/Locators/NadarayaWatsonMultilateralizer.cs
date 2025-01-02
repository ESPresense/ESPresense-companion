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
                // ------------------------------------------------------------------------
                // Choose a more typical kernel, e.g. a Gaussian kernel.
                // 'bandwidth' is often treated like a standard deviation σ for the kernel.
                //
                //   weight(d) = exp( - (d^2 / (2 * bandwidth^2)) )
                //
                // Note: if your config's bandwidth is extremely small, you could get
                // very large weights for near-zero distances. You may want to
                // enforce a minimum to avoid numeric blowups.
                // ------------------------------------------------------------------------
                var bandwidth = state.Config?.Locators?.NadarayaWatson?.Bandwidth ?? 1e-3; // Example: 0.001

                // If 'bandwidth' is effectively zero, enforce a minimum:
                if (bandwidth < 1e-9) bandwidth = 1e-9;

                var weights = nodes.Select((dn, i) =>
                {
                    // Gaussian kernel
                    var distance = dn.Distance;
                    var squaredRatio = (distance * distance) / (2.0 * bandwidth * bandwidth);
                    var distanceWeight = Math.Exp(-squaredRatio);

                    // If you have a separate weighting function, fold it in:
                    var positionWeight = state?.Weighting?.Get(i, nodes.Length) ?? 1.0;

                    return distanceWeight * positionWeight;
                }).ToArray();

                var totalWeight = weights.Sum();

                if (Math.Abs(totalWeight) < double.Epsilon)
                {
                    // This means every weight is effectively zero, so just revert to an average or guess
                    scenario.UpdateLocation(guess);
                }
                else
                {
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
                    // scenario.ReasonForExit = ...

                    // Example scaling for confidence
                    confidence = (int)Math.Min(100, Math.Max(10, 100.0 - (weightedError * 10)));
                }
            }
        }
        catch (Exception ex)
        {
            confidence = 0;
            scenario.UpdateLocation(new Point3D());
            Log.Error("Error finding location for {0}: {1}", device, ex.Message);
        }

        scenario.Confidence = confidence;

        if (confidence <= 0) return false;
        if (Math.Abs(scenario.Location.DistanceTo(scenario.LastLocation)) < 0.1) return false;

        scenario.Room = floor.Rooms.Values.FirstOrDefault(a =>
            a.Polygon?.EnclosesPoint(scenario.Location.ToPoint2D()) ?? false);

        return true;
    }
}
