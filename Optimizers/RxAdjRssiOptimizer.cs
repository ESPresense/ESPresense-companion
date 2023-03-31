using ESPresense.Models;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;
using Serilog;

namespace ESPresense.Optimizers;

public class RxAdjRssiOptimizer : IOptimizer
{
    public List<OptimizationResult> Optimize(OptimizationSnapshot os)
    {
        var absorption = 3d;

        List<OptimizationResult> or = new();

        foreach (var node in os.Nodes)
        {
            var rxNodes = os.Nodes.SelectMany(a => a.RxNodes.Values.Where(b => b.Current && b.Tx == node)).ToArray();
            var pos = rxNodes.Select(n => n.Location?.DistanceTo(node.Location) ?? double.NaN).ToArray();

            double Distance(Vector<double> x, OptRxNode dn) => Math.Pow(10, (dn.RefRssi - dn.Rssi + x[0]) / (10.0d * absorption));

            if (rxNodes.Length < 3) continue;

            try
            {
                var obj = ObjectiveFunction.Value(
                    x =>
                    {
                        if (x[0] < -30 || x[0] > 30) return double.PositiveInfinity;

                        var error = rxNodes
                            .Select((dn, i) => new { err = pos[i] - Distance(x, dn), weight = 1 })
                            .Average(a => a.weight * Math.Pow(a.err, 2));
                        //Console.WriteLine("{0,-20}> RxAdj: {1:00.000} Absorption: {2:00.000} Err: {3:00.000}", node.Id, x[0], x[0], error);
                        return error;
                    });

                var initialGuess = Vector<double>.Build.DenseOfArray(new[] { 0d });

                var solver = new NelderMeadSimplex(1e-7, 10000);
                var result = solver.FindMinimum(obj, initialGuess);
                or.Add(new OptimizationResult { NodeId = node.Id, RxAdjRssi = result.MinimizingPoint[0], Absorption = absorption, Error = result.FunctionInfoAtMinimum.Value });
            }
            catch (Exception ex)
            {
                Log.Error("Error optimizing {0}: {1}", node.Id, ex.Message);
            }

        }

        return or;
    }
}