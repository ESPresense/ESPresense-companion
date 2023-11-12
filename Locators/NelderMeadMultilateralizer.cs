﻿using ESPresense.Extensions;
using ESPresense.Models;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;
using MathNet.Spatial.Euclidean;
using Serilog;

namespace ESPresense.Locators;

public class NelderMeadMultilateralizer : ILocate
{
    private readonly Device _device;
    private readonly Floor _floor;
    private readonly State _state;

    public NelderMeadMultilateralizer(Device device, Floor floor, State state)
    {
        _device = device;
        _floor = floor;
        _state = state;
    }

    public bool Locate(Scenario scenario)
    {
        double Error(IList<double> x, DeviceNode dn) => new Point3D(x[0], x[1], x[2]).DistanceTo(dn.Node!.Location) * x[3] - dn.Distance;

        var confidence = scenario.Confidence;

        var nodes = _device.Nodes.Values.Where(a => a.Current && (a.Node?.Floors?.Contains(_floor) ?? false)).OrderBy(a => a.Distance).ToArray();
        var pos = nodes.Select(a => a.Node!.Location).ToArray();

        scenario.Minimum = nodes.Min(a => (double?)a.Distance);
        scenario.LastHit = nodes.Max(a => a.LastHit);
        scenario.Fixes = pos.Length;

        if (pos.Length <= 1)
        {
            scenario.Room = null;
            scenario.Confidence = 0;
            scenario.Error = null;
            scenario.Floor = null;
            return false;
        }

        scenario.Floor = _floor;

        var guess = confidence < 5
            ? Point3D.MidPoint(pos[0], pos[1])
            : scenario.Location;
        try
        {
            if (pos.Length < 3 || _floor.Bounds == null)
            {
                confidence = 1;
                scenario.Location = guess;
            }
            else
            {
                var lowerBound = Vector<double>.Build.DenseOfArray(new[] { _floor.Bounds[0].X, _floor.Bounds[0].Y, _floor.Bounds[0].Z, 0.5 });
                var upperBound = Vector<double>.Build.DenseOfArray(new[] { _floor.Bounds[1].X, _floor.Bounds[1].Y, _floor.Bounds[1].Z, 1.5 });
                var obj = ObjectiveFunction.Value(
                    x =>
                    {
                        var distanceFromBoundingBox = lowerBound.Subtract(x)
                            .PointwiseMaximum(x.Subtract(upperBound))
                            .PointwiseMaximum(0)
                            .L2Norm();
                        return (distanceFromBoundingBox > 0 ? Math.Pow(5, 1 + distanceFromBoundingBox) : 0) + Math.Pow(5 * (1 - x[3]), 2) + nodes
                            .Select((dn, i) => new { err = Error(x, dn), weight = _state?.Weighting?.Get(i, nodes.Length) ?? 1.0 })
                            .Average(a => a.weight * Math.Pow(a.err, 2));
                    });

                var initialGuess = Vector<double>.Build.DenseOfArray(new[]
                {
                    Math.Max(_floor.Bounds[0].X, Math.Min(_floor.Bounds[1].X, guess.X)),
                    Math.Max(_floor.Bounds[0].Y, Math.Min(_floor.Bounds[1].Y, guess.Y)),
                    Math.Max(_floor.Bounds[0].Z, Math.Min(_floor.Bounds[1].Z, guess.Z)),
                    scenario.Scale ?? 1.0
                });
                var solver = new NelderMeadSimplex(1e-7, 10000);
                var result = solver.FindMinimum(obj, initialGuess);
                var minimizingPoint = result.MinimizingPoint.PointwiseMinimum(upperBound).PointwiseMaximum(lowerBound);
                scenario.Location = new Point3D(minimizingPoint[0], minimizingPoint[1], minimizingPoint[2]);
                scenario.Scale = minimizingPoint[3];
                scenario.Fixes = pos.Length;
                scenario.Error = result.FunctionInfoAtMinimum.Value;
                scenario.Iterations = result switch
                {
                    MinimizationWithLineSearchResult mwl =>
                        mwl.IterationsWithNonTrivialLineSearch + mwl.TotalLineSearchIterations,
                    _ => result.Iterations
                };

                scenario.ReasonForExit = result.ReasonForExit;
                confidence = (int)Math.Min(100, Math.Max(10, 100.0 - (Math.Pow(scenario.Minimum ?? 1, 2) + Math.Pow(10 * (1 - (scenario.Scale ?? 1)), 2) + (scenario.Minimum + result.FunctionInfoAtMinimum.Value ?? 10.00))));
            }
        }
        catch (MaximumIterationsException)
        {
            scenario.ReasonForExit = ExitCondition.ExceedIterations;
            confidence = 1;
            scenario.Location = guess;
        }
        catch (Exception ex)
        {
            confidence = 0;
            scenario.Location = new Point3D();
            Log.Error("Error finding location for {0}: {1}", _device, ex.Message);
        }

        scenario.Confidence = confidence;

        if (confidence <= 0) return false;
        if (Math.Abs(scenario.Location.DistanceTo(scenario.LastLocation)) < 0.1) return false;
        scenario.Room = _floor.Rooms.Values.FirstOrDefault(a => a.Polygon?.EnclosesPoint(scenario.Location.ToPoint2D()) ?? false);
        return true;
    }
}