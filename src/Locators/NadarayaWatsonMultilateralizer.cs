using System;
using System.Linq;
using ESPresense.Extensions;
using ESPresense.Models;
using ESPresense.Services;
using ESPresense.Utils;
using ESPresense.Weighting;
using MathNet.Spatial.Euclidean;
using Serilog;

namespace ESPresense.Locators;

public class NadarayaWatsonMultilateralizer : ILocate
{
    private readonly Device _device;
    private readonly Floor _floor;
    private readonly State _state;
    private readonly NodeTelemetryStore _nts;
    private readonly IKernel _kernel;

    public NadarayaWatsonMultilateralizer(Device device, Floor floor, State state, NodeTelemetryStore nts, ConfigKernel? kernelConfig = null)
    {
        _device = device;
        _floor = floor;
        _state = state;
        _nts = nts;

        _kernel = kernelConfig?.Algorithm?.ToLowerInvariant() switch
        {
            "epanechnikov" => new EpanechnikovKernel(kernelConfig?.Props),
            "gaussian" => new GaussianKernel(kernelConfig?.Props),
            _ => new InverseSquareKernel(kernelConfig?.Props)
        };
    }

    public bool Locate(Scenario scenario)
    {
        var heard = _device.Nodes.Values
            .Where(n => n.Current && (n.Node?.Floors?.Contains(_floor) ?? false))
            .OrderBy(n => n.Distance)
            .ToArray();

        if (heard.Length <= 1)
        {
            scenario.Confidence = 0;
            scenario.Room = null;
            scenario.Error = null;
            return false;
        }

        scenario.Minimum = heard.Min(n => (double?)n.Distance);
        scenario.LastHit = heard.Max(n => n.LastHit);
        scenario.Fixes = heard.Length;
        scenario.Floor = _floor;

        Point3D est;

        try
        {
            if (heard.Length < 3 || _floor.Bounds == null)
            {
                est = Point3D.MidPoint(heard[0].Node!.Location, heard[1].Node!.Location);
                scenario.Error = null;
                scenario.PearsonCorrelation = null;
            }
            else
            {
                var weights = heard.Select(n => _kernel.Evaluate(n.Distance)).ToArray();
                var wSum = weights.Sum();

                if (wSum <= 0)
                {
                    est = Point3D.MidPoint(heard[0].Node!.Location, heard[1].Node!.Location);
                    scenario.Error = null;
                    scenario.PearsonCorrelation = null;
                }
                else
                {
                    est = new Point3D(
                        heard.Zip(weights, (n, w) => n.Node!.Location.X * w).Sum() / wSum,
                        heard.Zip(weights, (n, w) => n.Node!.Location.Y * w).Sum() / wSum,
                        heard.Zip(weights, (n, w) => n.Node!.Location.Z * w).Sum() / wSum
                    );

                    var weightedError = heard.Zip(weights, (n, w) =>
                    {
                        double diff = est.DistanceTo(n.Node!.Location) - n.Distance;
                        return w * diff * diff;
                    }).Sum() / wSum;

                    scenario.Error = weightedError;
                }
            }

            scenario.UpdateLocation(est);

            var measured = heard.Select(n => n.Distance).ToList();
            var calculated = heard.Select(n => est.DistanceTo(n.Node!.Location)).ToList();
            scenario.PearsonCorrelation = MathUtils.CalculatePearsonCorrelation(measured, calculated);

            int nodesPossibleOnline = _state.Nodes.Values
                .Count(n => (n.Floors?.Contains(_floor) ?? false) && _nts.Online(n.Id));

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
            scenario.Error = null;
            Log.Error("Locator error for {Device}: {Message}", _device, ex.Message);
        }

        if (scenario.Confidence <= 0) return false;
        if (scenario.Location.DistanceTo(scenario.LastLocation) < 0.1) return false;

        scenario.Room = _floor.Rooms.Values.FirstOrDefault(r =>
            r.Polygon?.EnclosesPoint(scenario.Location.ToPoint2D()) ?? false);

        return true;
    }
}
