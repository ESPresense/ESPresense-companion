﻿using ESPresense.Companion.Utils;
using ESPresense.Extensions;
using ESPresense.Models;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;
using MathNet.Spatial.Euclidean;
using Serilog;

namespace ESPresense.Locators;

public class MultiFloorMultilateralizer : ILocate
{
    private readonly Device _device;
    private readonly State _state;

    public MultiFloorMultilateralizer(Device device, State state)
    {
        _device = device;
        _state = state;
    }

    public bool Locate(Scenario scenario)
    {
        var confidence = scenario.Confidence;
        var solver = new NelderMeadSimplex(1e-7, 10000);

        double Error(IList<double> x, DeviceToNode dn) => (new Point3D(x[0], x[1], x[2]).DistanceTo(dn.Node!.Location)*x[3]) - dn.Distance;
        var nodes = _device.Nodes.Values.Where(a => a.Current).OrderBy(a => a.Distance).ToArray();

        var obj = ObjectiveFunction.Value(x =>
        {
            return Math.Pow(5*(1 - x[3]), 2) + nodes
                .Select((dn, i) => new { err = Error(x, dn), weight = _state.Weighting?.Get(i, nodes.Length) ?? 1.0 })
                .Average(a => a.weight * Math.Pow(a.err, 2));
        });
        var pos = _device.Nodes.Values.Where(a => a.Current).OrderBy(a => a.Distance).Select(a => a.Node!.Location).ToList();

        if (pos.Count <= 0)
        {
            scenario.Scale = 1;
            confidence = 0;
        }
        else
        {
            var guess = confidence < 5
                ? pos.Count switch
                {
                    >= 2 => Point3D.MidPoint(pos[0], pos[1]),
                    _ => pos[0]
                }
                : scenario.Location;

            try
            {
                if (pos.Count > 1)
                {
                    var init = Vector<double>.Build.Dense(new double[]
                    {
                        guess.X,
                        guess.Y,
                        guess.Z,
                        scenario.Scale ?? 1.0
                    });
                    var result = solver.FindMinimum(obj, init);
                    scenario.UpdateLocation(new Point3D(result.MinimizingPoint[0], result.MinimizingPoint[1], result.MinimizingPoint[2]));
                    scenario.Scale = result.MinimizingPoint[3];
                    scenario.Fixes = pos.Count;
                    scenario.Minimum = nodes.Min(a => (double?) a.Distance);
                    scenario.LastHit = nodes.Max(a => a.LastHit);

                    confidence = (int)Math.Max(Math.Min(100, (100 * pos.Count - 1) / 4.0), Math.Min(100, 100 * pos.Count / 4.0) - result.FunctionInfoAtMinimum.Value);
                }
                else
                {
                    confidence = 1;
                    scenario.Scale = 1;
                    scenario.UpdateLocation(guess);
                }
            }
            catch (Exception ex)
            {
                confidence = 1;
                scenario.Scale = 1;
                scenario.UpdateLocation(guess);
                Log.Error("Error finding location for {0}: {1}", _device, ex.Message);
            }
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

        var floors = _state.Floors.Values.Where(a => a.Contained(scenario.Location.Z));
        var room = floors.SelectMany(a => a.Rooms.Values).FirstOrDefault(a => a.Polygon?.EnclosesPoint(scenario.Location.ToPoint2D()) ?? false);
        if (scenario.Room != room) scenario.Room = room;
        Log.Debug("New location {0} {1}@{2}", _device, _device.Room?.Name ?? "Unknown", scenario.Location);
        return true;
    }
}