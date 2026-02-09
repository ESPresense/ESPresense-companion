using ESPresense.Utils;
using ESPresense.Extensions;
using ESPresense.Models;
using ESPresense.Weighting;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;
using MathNet.Spatial.Euclidean;
using Serilog;

namespace ESPresense.Locators;

public class MultiFloorMultilateralizer : ILocate
{
    private readonly Device _device;
    private readonly State _state;
    private readonly IWeighting _weighting;

    public MultiFloorMultilateralizer(Device device, State state)
    {
        _device = device;
        _state = state;

        // Use NelderMead weighting config as default for multi-floor
        var w = state.Config?.Locators?.NelderMead?.Weighting;
        _weighting = w?.Algorithm switch
        {
            "equal" => new EqualWeighting(),
            "linear" => new LinearWeighting(w?.Props),
            "gaussian" => new GaussianWeighting(w?.Props),
            "exponential" => new ExponentialWeighting(w?.Props),
            _ => new GaussianWeighting(w?.Props),
        };
    }

    public bool Locate(Scenario scenario)
    {
        var confidence = scenario.Confidence;
        var solver = new NelderMeadSimplex(1e-7, 10000);

        double Error(IList<double> x, DeviceToNode dn) => (new Point3D(x[0], x[1], x[2]).DistanceTo(dn.Node!.Location)*x[3]) - dn.Distance;
        var nodes = _device.Nodes.Values.Where(a => a.Current).OrderBy(a => a.Distance).ToArray();
        var weights = nodes.Select((dn, i) => _weighting.Get(i, nodes.Length)).ToArray();
        var weightSum = weights.Sum();
        if (weightSum <= 0) weightSum = 1;

        var obj = ObjectiveFunction.Value(x =>
        {
            return Math.Pow(5*(1 - x[3]), 2) + nodes
                .Select((dn, i) => new { err = Error(x, dn), weight = weights[i] })
                .Sum(a => a.weight * Math.Pow(a.err, 2)) / weightSum;
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
                    scenario.Error = result.FunctionInfoAtMinimum.Value;

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

                    // Calculate number of possible nodes (all nodes since MultiFloor is floor-agnostic)
                    int nodesPossibleOnline = _state.Nodes.Values.Count();

                    // Use the centralized confidence calculation
                    confidence = MathUtils.CalculateConfidence(
                        scenario.Error,
                        scenario.PearsonCorrelation,
                        nodes.Length,
                        nodesPossibleOnline
                    );
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

        if (confidence <= 0) return false;
        if (Math.Abs(scenario.Location.DistanceTo(scenario.LastLocation)) < 0.1) return false;

        var floors = _state.Floors.Values.Where(a => a.Contained(scenario.Location.Z));
        var room = floors.SelectMany(a => a.Rooms.Values).FirstOrDefault(a => a.Polygon?.EnclosesPoint(scenario.Location.ToPoint2D()) ?? false);
        if (scenario.Room != room) scenario.Room = room;
        Log.Debug("New location {0} {1}@{2}", _device, _device.Room?.Name ?? "Unknown", scenario.Location);
        return true;
    }
}
