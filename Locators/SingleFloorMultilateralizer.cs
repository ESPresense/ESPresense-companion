using ESPresense.Extensions;
using ESPresense.Models;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;
using MathNet.Spatial.Euclidean;
using Serilog;
using YamlDotNet.Serialization.NodeDeserializers;

namespace ESPresense.Locators;

public class SingleFloorMultilateralizer : ILocate
{
    private readonly Device _device;
    private readonly Floor _floor;

    public SingleFloorMultilateralizer(Device device, Floor floor)
    {
        _device = device;
        _floor = floor;
    }

    public bool Locate(Scenario scenario)
    {
        var confidence = scenario.Confidence;
        var solver = new NelderMeadSimplex(1e-7, 10000);

        var nodes = _device.Nodes.Values.Where(a => a.Current && (a.Node?.Floors?.Contains(_floor) ?? false)).OrderBy(a => a.Distance).ToArray();
        var pos = nodes.Select(a => a.Node!.Location).ToList();
        var obj = ObjectiveFunction.Value(x => nodes.Select((dn,i)=> new {dn, weight = Math.Pow((float)nodes.Length - i, 3) / Math.Pow(nodes.Length,3)}).Sum(a =>
        {
            var distanceTo = new Point3D(x[0], x[1], x[2]).DistanceTo(a.dn.Node!.Location) - a.dn.Distance;
            return a.weight * Math.Pow(distanceTo, distanceTo > 0 ? 2 : 10);
        }));

        if (pos.Count <= 0)
        {
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
                    });
                    var result = solver.FindMinimum(obj, init);
                    scenario.Location = new Point3D(result.MinimizingPoint[0], result.MinimizingPoint[1], result.MinimizingPoint[2]);
                    scenario.Fixes = pos.Count;
                    confidence = (int)Math.Min(Math.Min(100, (100 * pos.Count - 1) / 4.0), Math.Min(100, 100 * pos.Count / 4.0) - result.FunctionInfoAtMinimum.Value);
                }
                else
                {
                    confidence = 1;
                    scenario.Location = guess;
                }
            }
            catch (Exception ex)
            {
                confidence = 1;
                scenario.Location = guess;
                Log.Error("Error finding location for {0}: {1}", _device, ex.Message);
            }
        }

        scenario.Confidence = confidence;

        if (confidence <= 0) return false;
        if (Math.Abs(scenario.Location.DistanceTo(scenario.LastLocation)) < 0.1) return false;

        var room = scenario.Room = _floor.Rooms.Values.FirstOrDefault(a => a.Polygon?.EnclosesPoint(scenario.Location.ToPoint2D()) ?? false);
        Log.Debug("New location {0} {1}@{2}", _device, _device.Room?.Name ?? "Unknown", scenario.Location);

        scenario.Confidence = room == null ? 5 : confidence;
        return true;
    }
}