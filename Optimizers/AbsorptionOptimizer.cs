using ESPresense.Locators;
using ESPresense.Models;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;
using Serilog;

namespace ESPresense.Optimizers;

public class AbsorptionOptimizer : IOptimizer
{
    private readonly State _state;
    //private readonly GaussianWeighting _weighting;

    public AbsorptionOptimizer(State state)
    {
        _state = state;
    }

    public (double rxAdjRssi, double absorption, double error)? Optimize(Node node, NodeSettings? ns)
    {
        var absorption = ns?.Absorption ?? 3d;
        if (absorption >= 6 || absorption <= 1) absorption = 3;
        var deviceNodes = _state.Devices.Values.Where(a => a.Id.StartsWith("node:")).SelectMany(a=> a.Nodes.Values.Where(b => b.Current && b.Node == node)).ToArray();
        var nodes = deviceNodes.SelectMany(d => _state.Nodes.Values.Where(n => "node:" + n.Id == d?.Device?.Id)).ToArray();
        deviceNodes = nodes.Select(n => deviceNodes.First(a => a.Device?.Id == "node:"+ n.Id)).ToArray();
        var pos = nodes.Select(n => n.Location.DistanceTo(node.Location)).ToArray();

        double Distance(Vector<double> x, DeviceNode dn) => Math.Pow(10, (dn.RefRssi - dn.Rssi) / (10.0d * x[0]));

        if (nodes.Length < 3) return null;

        try
        {
            var obj = ObjectiveFunction.Value(
                x =>
                {
                    if (x[0] <= 1 || x[0] >= 6) return double.PositiveInfinity;

                    var error = deviceNodes
                        .Select((dn, i) => new { err = pos[i] - Distance(x, dn), weight = 1})
                        .Average(a => a.weight * Math.Pow(a.err, 2));
                    //Console.WriteLine("{0,-20}> Absorption: {1:#0.000, 10} Err: {2:##0.0000000000000000}", node.Id, x[0], error);
                    return error;
                });

            var initialGuess = Vector<double>.Build.DenseOfArray(new[] { absorption });

            var solver = new NelderMeadSimplex(1e-4, 10000);
            var result = solver.FindMinimum(obj, initialGuess);
            return (ns?.RxAdjRssi ?? 0, result.MinimizingPoint[0], result.FunctionInfoAtMinimum.Value);
        }
        catch (Exception ex)
        {
            Log.Error("Error optimizing {0}: {1}", node.Id, ex.Message);
        }

        return null;
    }
}