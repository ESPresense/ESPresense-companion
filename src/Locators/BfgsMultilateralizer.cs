﻿using ESPresense.Utils;
using ESPresense.Extensions;
using ESPresense.Models;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;
using MathNet.Spatial.Euclidean;
using Serilog;

namespace ESPresense.Locators;

public class BfgsMultilateralizer : BaseMultilateralizer
{
    public BfgsMultilateralizer(Device device, Floor floor, State state)
        : base(device, floor, state)
    {
    }

    public override bool Locate(Scenario scenario)
    {
        double Weight(int index, int total) => Math.Pow((float)total - index, 3) / Math.Pow(total, 3);
        double Error(IList<double> x, DeviceToNode dn) => new Point3D(x[0], x[1], x[2]).DistanceTo(dn.Node!.Location) - dn.Distance;

        if (!InitializeScenario(scenario, out var nodes, out var guess))
            return false;

        int confidence = scenario.Confidence ?? 0;
        try
        {
            if (nodes.Length < 3 || Floor.Bounds == null)
            {
                confidence = 1;
                scenario.UpdateLocation(guess);
            }
            else
            {
                var lowerBound = Vector<double>.Build.DenseOfArray(new[] { Floor.Bounds[0].X, Floor.Bounds[0].Y, Floor.Bounds[0].Z });
                var upperBound = Vector<double>.Build.DenseOfArray(new[] { Floor.Bounds[1].X, Floor.Bounds[1].Y, Floor.Bounds[1].Z });
                var obj = ObjectiveFunction.Gradient(
                    x => nodes
                        .Select((dn, i) => new { err = Error(x, dn), weight = Weight(i, nodes.Length) })
                        .Sum(a => a.weight * Math.Pow(a.err, 2))
                    ,
                    x =>
                    {
                        var known = nodes.Select(a => a.Node!.Location.ToVector()).ToList();
                        var gradient = new double[3];
                        for (var i = 0; i < 3; i++)
                            gradient[i] = known.Select((a, j) => (2 * x[i] - 2 * known[j][i]) * Weight(j, known.Count)).Average();
                        return Vector<double>.Build.Dense(gradient);
                    });

                var clampedGuess = ClampToFloorBounds(guess);
                var initialGuess = Vector<double>.Build.DenseOfArray(new[]
                {
                    clampedGuess.X,
                    clampedGuess.Y,
                    clampedGuess.Z
                });
                var solver = new BfgsBMinimizer(0, 0.25, 0.25, 1000);
                var result = solver.FindMinimum(obj, lowerBound, upperBound, initialGuess);
                scenario.UpdateLocation(new Point3D(result.MinimizingPoint[0], result.MinimizingPoint[1], result.MinimizingPoint[2]));
                scenario.Fixes = nodes.Length;
                scenario.Error = result.FunctionInfoAtMinimum.Value;
                scenario.Iterations = result switch
                {
                    MinimizationWithLineSearchResult mwl =>
                        mwl.IterationsWithNonTrivialLineSearch + mwl.TotalLineSearchIterations,
                    _ => result.Iterations
                };

                scenario.ReasonForExit = result.ReasonForExit;
                confidence = (int)Math.Max(10, Math.Min(100, Math.Min(100, 100 * nodes.Length / 4.0) - result.FunctionInfoAtMinimum.Value));
            }
        }
        catch (Exception ex)
        {
            confidence = HandleLocatorException(ex, scenario, guess);
        }

        CalculateAndSetPearsonCorrelation(scenario, nodes);

        return FinalizeScenario(scenario, confidence);
    }
}