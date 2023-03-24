using ESPresense.Locators;
using ESPresense.Models;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;
using Serilog;

namespace ESPresense.Optimizers;

public class TxAdjOptimizer : IOptimizer
{
    private readonly State _state;
    private readonly GaussianWeighting _weighting;

    public TxAdjOptimizer(State state)
    {
        _state = state;
        _weighting = new GaussianWeighting(null);
    }

    public (double rxAdj, double absorption, double error)? Optimize(Node node)
    {
        if (!_state.Devices.TryGetValue("node:" + node.Id, out var device)) return null;

        double Distance(Vector<double> x, DeviceNode dn) => Math.Pow(10, (dn.RefRssi - dn.Rssi - x[0]) / (10.0d * 3));
        var nodes = device.Nodes.Values.Where(a => a.Current && (a.Node?.Floors?.Intersect(node.Floors ?? Enumerable.Empty<Floor>()) ?? Enumerable.Empty<Floor>()).Any()).OrderBy(a => a.Distance).ToArray();
        if (nodes.Length < 3) return null;
        var pos = nodes.Select(a => a.Node!.Location.DistanceTo(node.Location)).ToArray();

        try
        {
            var obj = ObjectiveFunction.Value(
                x =>
                {
                    if (x[0] < -30 || x[0] > 30) return double.PositiveInfinity;

                    var error = nodes
                        .Select((dn, i) => new { err = pos[i] - Distance(x, dn), weight = _weighting?.Get(i, nodes.Length) ?? 1.0 })
                        .Average(a => a.weight * Math.Pow(a.err, 2));
                    //Console.WriteLine("{0,-20}> RxAdj: {1:00.000} Absorption: {2:00.000} Err: {3:00.000}", node.Id, x[0], x[0], error);
                    return error;
                });

            var initialGuess = Vector<double>.Build.DenseOfArray(new[] { 0d });

            var solver = new NelderMeadSimplex(1e-7, 10000);
            var result = solver.FindMinimum(obj, initialGuess);
            return (result.MinimizingPoint[0],3, result.FunctionInfoAtMinimum.Value);
        }
        catch (Exception ex)
        {
            Log.Error("Error optimizing {0}: {1}", device, ex.Message);
        }

        return null;
    }
}