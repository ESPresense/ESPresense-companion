using System;
using System.Linq;
using ESPresense.Utils;
using ESPresense.Extensions;
using ESPresense.Models;
using MathNet.Spatial.Euclidean;
using Serilog;
using ESPresense.Services;

namespace ESPresense.Locators;

public class NadarayaWatsonMultilateralizer(Device device, Floor floor, State state, NodeTelemetryStore nts) : ILocate
{
    public bool Locate(Scenario scenario)
    {
        var heard = device.Nodes.Values
            .Where(n => n.Current && (n.Node?.Floors?.Contains(floor) ?? false))
            .OrderBy(n => n.Distance)
            .ToArray();

        if (heard.Length <= 1)
        {
            scenario.Confidence = 0;
            scenario.Room       = null;
            scenario.Error      = null;
            return false;
        }

        scenario.Minimum = heard.Min(n => (double?)n.Distance);
        scenario.LastHit = heard.Max(n => n.LastHit);
        scenario.Fixes   = heard.Length;
        scenario.Floor   = floor;

        Point3D est;
        double  weightedError = 0;

        try
        {
            if (heard.Length < 3 || floor.Bounds == null)
            {
                est = Point3D.MidPoint(heard[0].Node!.Location, heard[1].Node!.Location);
                scenario.Error = null;
                scenario.PearsonCorrelation = null;
            }
            else
            {
                const double EPS = 1e-6;
                var weights = heard.Select(n => 1.0 / (Math.Pow(n.Distance, 2) + EPS)).ToArray();
                var wSum = weights.Sum();

                est = new Point3D(
                    heard.Zip(weights, (n, w) => n.Node!.Location.X * w).Sum() / wSum,
                    heard.Zip(weights, (n, w) => n.Node!.Location.Y * w).Sum() / wSum,
                    heard.Zip(weights, (n, w) => n.Node!.Location.Z * w).Sum() / wSum
                );

                weightedError = heard.Zip(weights, (n, w) =>
                {
                    double diff = est.DistanceTo(n.Node!.Location) - n.Distance;
                    return w * diff * diff;
                }).Sum() / wSum;

                scenario.Error = weightedError;
            }

            scenario.UpdateLocation(est);

            var measured   = heard.Select(n => n.Distance).ToList();
            var calculated = heard.Select(n => est.DistanceTo(n.Node!.Location)).ToList();
            scenario.PearsonCorrelation = MathUtils.CalculatePearsonCorrelation(measured, calculated);

            // Get count of possible online nodes for this floor
            int nodesPossibleOnline = state.Nodes.Values
                .Count(n =>
                    (n.Floors?.Contains(floor) ?? false) &&
                    (nts.Online(n.Id)));

            // Use the centralized confidence calculation
            scenario.Confidence = MathUtils.CalculateConfidence(
                scenario.Error,
                scenario.PearsonCorrelation,
                heard.Length,
                nodesPossibleOnline
            );
        }
        catch (Exception ex)
        {
            scenario.UpdateLocation(scenario.Location); // revert to last good
            scenario.Confidence = 0;
            scenario.Error      = null;
            Log.Error("Locator error for {Device}: {Message}", device, ex.Message);
        }

        if (scenario.Confidence <= 0) return false;
        if (scenario.Location.DistanceTo(scenario.LastLocation) < 0.1) return false;

        scenario.Room = floor.Rooms.Values.FirstOrDefault(r =>
            r.Polygon?.EnclosesPoint(scenario.Location.ToPoint2D()) ?? false);

        return true;
    }
}
