using ESPresense.Models;
using ESPresense.Utils;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;
using Serilog;
using ESPresense.Extensions;

namespace ESPresense.Optimizers;

public class GlobalAbsorptionRxTxOptimizer : IOptimizer
{
    private readonly State _state;

    // Parameter to control asymmetric error weighting
    // Higher value means we penalize "impossible" cases more strongly
    private const double AsymmetricErrorFactor = 5;
    // Capped error value to prevent extreme outliers from skewing results
    private const double CappedError = 2560;

    public GlobalAbsorptionRxTxOptimizer(State state)
    {
        _state = state;
    }

    public string Name => "Global Absorption Rx Adj + Antenna";

    public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings)
    {
        var results = new OptimizationResults();
        var optimization = _state.Config?.Optimization;
        if (optimization == null) return results;

        var allMeasures = os.ByRx().SelectMany(g => g).ToList();
        if (allMeasures.Count < 3)
        {
            Log.Warning("Not enough valid measurements for optimization. Found {Count} measurements, need at least 3. Add more BLE beacons or ESPresense nodes.", allMeasures.Count);
            return results;
        }

        var uniqueRxIds = allMeasures.Select(m => m.Rx.Id).Distinct().ToList();
        var directionalNodeIds = allMeasures
            .SelectMany(m => new[] { m.Rx, m.Tx })
            .Where(n => n.IsNode && n.HasDirectionalAntenna)
            .Select(n => n.Id)
            .Distinct()
            .ToList();

        var weights = PreCalculateWeights(allMeasures);

        int absorptionIndex = 0;
        var rxAdjIndexMap = new Dictionary<string, int>();
        int paramIndex = 1;
        foreach (var rxId in uniqueRxIds)
            rxAdjIndexMap[rxId] = paramIndex++;

        var dirIndexMap = new Dictionary<string, int>();
        foreach (var nodeId in directionalNodeIds)
        {
            dirIndexMap[nodeId] = paramIndex;
            paramIndex += 3; // sinAz, cosAz, sinEl
        }

        int totalParams = paramIndex;

        double Eval(double[] x)
        {
            double absorption = x[absorptionIndex];
            double squaredErrorSum = 0.0;
            double weightSum = 0.0;
            double penalty = 0.0;

            // Soft boundary penalties instead of PositiveInfinity to keep gradients finite
            if (absorption < optimization.AbsorptionMin)
                penalty += 1000.0 * Math.Pow(optimization.AbsorptionMin - absorption, 2);
            if (absorption > optimization.AbsorptionMax)
                penalty += 1000.0 * Math.Pow(absorption - optimization.AbsorptionMax, 2);

            foreach (var rxId in uniqueRxIds)
            {
                var rxAdj = x[rxAdjIndexMap[rxId]];
                if (rxAdj < optimization.RxAdjRssiMin)
                    penalty += 1000.0 * Math.Pow(optimization.RxAdjRssiMin - rxAdj, 2);
                if (rxAdj > optimization.RxAdjRssiMax)
                    penalty += 1000.0 * Math.Pow(rxAdj - optimization.RxAdjRssiMax, 2);
            }

            foreach (var nodeId in directionalNodeIds)
            {
                int baseIdx = dirIndexMap[nodeId];
                double sa = x[baseIdx];
                double ca = x[baseIdx + 1];
                double dev = sa * sa + ca * ca - 1.0;
                penalty += 0.1 * dev * dev;
            }

            foreach (var measure in allMeasures)
            {
                if (measure.Rx?.Location == null || measure.Tx?.Location == null)
                    continue;

                double rxAdj = x[rxAdjIndexMap[measure.Rx.Id]];
                double mapDistance = Math.Max(measure.Rx.Location.DistanceTo(measure.Tx.Location), 0.1);
                double gainDb = ComputeLinkGainDb(x, dirIndexMap, measure);
                if (double.IsNaN(gainDb) || double.IsInfinity(gainDb)) continue;
                double predictedRssi = measure.RefRssi + gainDb - 10 * absorption * Math.Log10(mapDistance);
                double diff = predictedRssi - measure.GetAdjustedRssi(rxAdj);

                double weight = weights[measure];
                weightSum += weight;
                double error = diff < 0
                    ? Math.Min(AsymmetricErrorFactor * diff * diff, CappedError)
                    : Math.Min(diff * diff, CappedError);
                squaredErrorSum += weight * error;
            }

            return (weightSum > 0 ? squaredErrorSum / weightSum : squaredErrorSum) + penalty;
        }

        var objective = ObjectiveFunction.Gradient(
            x => Eval(x.ToArray()),
            x =>
            {
                var xa = x.ToArray();
                var gradient = Vector<double>.Build.Dense(totalParams);
                const double h = 1e-5;
                double baseVal = Eval(xa);
                for (int i = 0; i < totalParams; i++)
                {
                    var xp = (double[])xa.Clone();
                    xp[i] += h;
                    gradient[i] = (Eval(xp) - baseVal) / h;
                }
                return gradient;
            });

        var lower = new double[totalParams];
        var upper = new double[totalParams];
        var initial = new double[totalParams];

        lower[absorptionIndex] = optimization.AbsorptionMin;
        upper[absorptionIndex] = optimization.AbsorptionMax;
        var validAbsorptions = existingSettings.Values
            .Select(ns => ns.Calibration?.Absorption)
            .Where(a => a.HasValue)
            .Select(a => a!.Value)
            .ToList();
        initial[absorptionIndex] = Math.Clamp(
            validAbsorptions.Any() ? validAbsorptions.Average() : (optimization.AbsorptionMin + optimization.AbsorptionMax) / 2.0,
            optimization.AbsorptionMin,
            optimization.AbsorptionMax);

        foreach (var rxId in uniqueRxIds)
        {
            int idx = rxAdjIndexMap[rxId];
            lower[idx] = optimization.RxAdjRssiMin;
            upper[idx] = optimization.RxAdjRssiMax;
            existingSettings.TryGetValue(rxId, out var nodeSettings);
            initial[idx] = Math.Clamp(nodeSettings?.Calibration?.RxAdjRssi ?? 0, optimization.RxAdjRssiMin, optimization.RxAdjRssiMax);
        }

        foreach (var nodeId in directionalNodeIds)
        {
            int idx = dirIndexMap[nodeId];
            lower[idx] = -2.0;
            upper[idx] = 2.0;
            lower[idx + 1] = -2.0;
            upper[idx + 1] = 2.0;
            lower[idx + 2] = -1.0;
            upper[idx + 2] = 1.0;

            existingSettings.TryGetValue(nodeId, out var nodeSettings);
            double azDeg = nodeSettings?.Calibration?.Azimuth ?? 0.0;
            double elDeg = nodeSettings?.Calibration?.Elevation ?? 0.0;
            double azRad = azDeg * Math.PI / 180.0;
            double elRad = elDeg * Math.PI / 180.0;
            initial[idx] = Math.Sin(azRad);
            initial[idx + 1] = Math.Cos(azRad);
            initial[idx + 2] = Math.Sin(elRad);
        }

        try
        {
            var solver = new BfgsBMinimizer(1e-7, 1e-7, 1e-7, 10000);
            var result = solver.FindMinimum(
                objective,
                Vector<double>.Build.DenseOfArray(lower),
                Vector<double>.Build.DenseOfArray(upper),
                Vector<double>.Build.DenseOfArray(initial));

            double absorption = Math.Clamp(result.MinimizingPoint[absorptionIndex], optimization.AbsorptionMin, optimization.AbsorptionMax);
            foreach (var rxId in uniqueRxIds)
            {
                double rxAdj = Math.Clamp(result.MinimizingPoint[rxAdjIndexMap[rxId]], optimization.RxAdjRssiMin, optimization.RxAdjRssiMax);
                var n = results.Nodes.GetOrAdd(rxId);
                n.RxAdjRssi = rxAdj;
                n.Absorption = absorption;
                n.TxRefRssi = null;
                n.Error = result.FunctionInfoAtMinimum.Value;
            }

            foreach (var nodeId in directionalNodeIds)
            {
                int idx = dirIndexMap[nodeId];
                double azRad = Math.Atan2(result.MinimizingPoint[idx], result.MinimizingPoint[idx + 1]);
                double elRad = Math.Asin(Math.Clamp(result.MinimizingPoint[idx + 2], -1.0, 1.0));
                double azDeg = azRad * 180.0 / Math.PI;
                if (azDeg < 0) azDeg += 360.0;

                var n = results.Nodes.GetOrAdd(nodeId);
                n.Azimuth = azDeg;
                n.Elevation = elRad * 180.0 / Math.PI;
                n.Error = result.FunctionInfoAtMinimum.Value;
                if (n.Absorption == null)
                    n.Absorption = absorption;
            }

            Log.Debug("{Name} completed with error: {Error}, absorption: {Absorption}", Name, result.FunctionInfoAtMinimum.Value, absorption);
        }
        catch (Exception ex)
        {
            Log.Error("Error in {Name}: {Message}", Name, ex.Message);
        }

        return results;
    }

    private Dictionary<Measure, double> PreCalculateWeights(List<Measure> allMeasures)
    {
        var measureWeights = new Dictionary<Measure, double>();
        double totalWeight = 0;

        foreach (var measure in allMeasures)
        {
            double weight = 1.0;
            if (measure.RssiVar > 0)
                weight = 1.0 / Math.Max(measure.RssiVar.Value, 0.1);
            measureWeights[measure] = weight;
            totalWeight += weight;
        }

        if (totalWeight > 0)
        {
            foreach (var measure in allMeasures)
                measureWeights[measure] = measureWeights[measure] / totalWeight * allMeasures.Count;
        }

        return measureWeights;
    }

    private static double ComputeLinkGainDb(double[] x, Dictionary<string, int> dirIndexMap, Measure measure)
    {
        double dx = measure.Tx.Location.X - measure.Rx.Location.X;
        double dy = measure.Tx.Location.Y - measure.Rx.Location.Y;
        double dz = measure.Tx.Location.Z - measure.Rx.Location.Z;

        double gain = 0.0;
        gain += ComputeNodeGainDb(x, dirIndexMap, measure.Rx, dx, dy, dz);
        gain += ComputeNodeGainDb(x, dirIndexMap, measure.Tx, -dx, -dy, -dz);
        return gain;
    }

    private static double ComputeNodeGainDb(double[] x, Dictionary<string, int> dirIndexMap, OptNode node, double dx, double dy, double dz)
    {
        if (!node.IsNode || !node.HasDirectionalAntenna || !dirIndexMap.TryGetValue(node.Id, out var idx))
            return 0.0;

        double sinAz = x[idx];
        double cosAz = x[idx + 1];
        double sinEl = x[idx + 2];

        double azNorm = Math.Sqrt(sinAz * sinAz + cosAz * cosAz);
        if (azNorm < 1e-9)
        {
            sinAz = 0.0;
            cosAz = 1.0;
        }
        else
        {
            sinAz /= azNorm;
            cosAz /= azNorm;
        }

        double cosEl = Math.Sqrt(Math.Max(1.0 - sinEl * sinEl, 0.0));
        return MathUtils.ComputeGainDb(
            sinAz * cosEl,
            cosAz * cosEl,
            sinEl,
            dx,
            dy,
            dz,
            10.0 * Math.Log10(node.GMax),
            node.PatternExponent,
            node.BackLossDb);
    }
}
