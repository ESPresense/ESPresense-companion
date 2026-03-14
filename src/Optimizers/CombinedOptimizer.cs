using ESPresense.Models;
using ESPresense.Utils;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;
using Serilog;

// Type aliases for per-node azimuth/elevation collections (semantic clarity for 3-tuple returns)
using NodeAbsorptionMap = System.Collections.Generic.Dictionary<string, double>;
using NodeAzimuthMap = System.Collections.Generic.Dictionary<string, double>;
using NodeElevationMap = System.Collections.Generic.Dictionary<string, double>;

namespace ESPresense.Optimizers;

public class CombinedOptimizer : IOptimizer
{
    private readonly State _state;

    public CombinedOptimizer(State state)
    {
        _state = state;
    }

    public string Name => "Two-Step Optimized Combined RxAdjRssi and Absorption";

    public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings)
    {
        var results = new OptimizationResults();
        var optimization = _state.Config?.Optimization;

        var allNodes = os.ByRx().SelectMany(g => g).ToList();
        var uniqueDeviceIds = allNodes.SelectMany(n => new[] { n.Rx.Id, n.Tx.Id }).Distinct().ToList();

        if (allNodes.Count < 3) return results;

        try
        {
            if (optimization == null) return results;

            // Step 1: Optimize RxAdjRssi and path-specific absorptions
            var (rxAdjRssiDict, pathAbsorptionDict, _) = OptimizeRxAdjRssiAndPathAbsorption(allNodes, uniqueDeviceIds, optimization, existingSettings);

            // Step 2: Optimize node-specific absorptions and antenna pointing (sinAz/cosAz/sinEl) while keeping RxAdjRssi constant
            var (nodeAbsorptions, nodeAzimuths, nodeElevations, nodeError) = OptimizeNodeAbsorptions(allNodes, uniqueDeviceIds, rxAdjRssiDict, pathAbsorptionDict, optimization, existingSettings);

            // Process and store results
            foreach (var deviceId in uniqueDeviceIds)
            {
                if (rxAdjRssiDict.TryGetValue(deviceId, out var rxAdjRssi) &&
                    nodeAbsorptions.TryGetValue(deviceId, out var absorption))
                {
                    var proposed = new ProposedValues
                    {
                        RxAdjRssi = rxAdjRssi,
                        Absorption = absorption,
                        Error = nodeError
                    };
                    if (nodeAzimuths.TryGetValue(deviceId, out var az)) proposed.Azimuth = az;
                    if (nodeElevations.TryGetValue(deviceId, out var el)) proposed.Elevation = el;
                    results.Nodes[deviceId] = proposed;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Error in combined optimization: {0}", ex.Message);
        }

        return results;
    }

    private (Dictionary<string, double> RxAdjRssi, Dictionary<(string, string), double> PathAbsorption, double Error)
        OptimizeRxAdjRssiAndPathAbsorption(List<Measure> allNodes, List<string> uniqueDeviceIds, ConfigOptimization optimization, Dictionary<string, NodeSettings> existingSettings)
    {
        var pathPairs = new HashSet<(string, string)>(allNodes.Select(n => (Min(n.Rx.Id, n.Tx.Id), Max(n.Rx.Id, n.Tx.Id))));

        var obj = ObjectiveFunction.Value(x =>
        {
            var rxAdjRssiDict = new Dictionary<string, double>();
            var absorptionDict = new Dictionary<(string, string), double>();

            for (int i = 0; i < uniqueDeviceIds.Count; i++)
            {
                var rxAdjRssi = x[i];
                // Bounds should always come from global config
                double rxAdjMin = optimization?.RxAdjRssiMin ?? -15;
                double rxAdjMax = optimization?.RxAdjRssiMax ?? 25;
                if (rxAdjRssi < rxAdjMin || rxAdjRssi > rxAdjMax)
                    return double.PositiveInfinity;
                rxAdjRssiDict[uniqueDeviceIds[i]] = rxAdjRssi;
            }

            int offset = uniqueDeviceIds.Count;
            foreach (var pair in pathPairs)
            {
                var absorption = x[offset++];
                if (absorption <= optimization?.AbsorptionMin || absorption >= optimization?.AbsorptionMax)
                    return double.PositiveInfinity;
                absorptionDict[pair] = absorption;
            }

            return CalculateError(allNodes, rxAdjRssiDict, pathAbsorptionDict: absorptionDict);
        });

        var initialGuess = Vector<double>.Build.Dense(uniqueDeviceIds.Count + pathPairs.Count);
        for (int i = 0; i < uniqueDeviceIds.Count; i++)
        {
            existingSettings.TryGetValue(uniqueDeviceIds[i], out var nodeSettings);
            // Initial guess uses node setting if available, else 0
            // Clamp initial guess within global bounds
            initialGuess[i] = Math.Clamp(nodeSettings?.Calibration?.RxAdjRssi ?? 0, optimization.RxAdjRssiMin, optimization.RxAdjRssiMax);
        }
        for (int i = uniqueDeviceIds.Count; i < initialGuess.Count; i++)
        {
            // Clamp initial guess within global bounds (Path absorption uses global midpoint, clamped)
            initialGuess[i] = Math.Clamp((optimization?.AbsorptionMax - optimization?.AbsorptionMin) / 2 + optimization?.AbsorptionMin ?? 3d, optimization.AbsorptionMin, optimization.AbsorptionMax);
        }

        var solver = new NelderMeadSimplex(1e-7, 20000);
        var result = solver.FindMinimum(obj, initialGuess);

        var rxAdjRssiDict = new Dictionary<string, double>();
        var pathAbsorptionDict = new Dictionary<(string, string), double>();

        for (int i = 0; i < uniqueDeviceIds.Count; i++)
        {
            rxAdjRssiDict[uniqueDeviceIds[i]] = result.MinimizingPoint[i];
        }

        int pathOffset = uniqueDeviceIds.Count;
        foreach (var pair in pathPairs)
        {
            pathAbsorptionDict[pair] = result.MinimizingPoint[pathOffset++];
        }

        return (rxAdjRssiDict, pathAbsorptionDict, result.FunctionInfoAtMinimum.Value);
    }

    private (NodeAbsorptionMap Absorptions, NodeAzimuthMap Azimuths, NodeElevationMap Elevations, double Error) OptimizeNodeAbsorptions(List<Measure> allNodes, List<string> uniqueDeviceIds,
        Dictionary<string, double> rxAdjRssiDict, Dictionary<(string, string), double> pathAbsorptionDict, ConfigOptimization optimization, Dictionary<string, NodeSettings> existingSettings)
    {
        int absorptionCount = uniqueDeviceIds.Count;
        var directionalDeviceIds = uniqueDeviceIds
            .Where(id => allNodes.Any(m => m.Rx.Id == id && m.Rx.IsNode && m.Rx.HasDirectionalAntenna))
            .ToList();
        int directionalCount = directionalDeviceIds.Count;
        int vecLen = Step2Layout.VectorLength(absorptionCount, directionalCount);

        // Use ObjectiveFunction.Gradient() with gradient over the full 4N vector.
        // Both absorption and antenna direction parameters are optimised simultaneously
        // using gain-corrected error plus unit-circle regularisation.
        var obj = ObjectiveFunction.Gradient(
            x => {
                var xa = x.ToArray();
                var nodeAbsorptionDict = new Dictionary<string, double>();
                for (int i = 0; i < absorptionCount; i++)
                {
                    var absorption = xa[Step2Layout.AbsIndex(i, absorptionCount)];
                    double absorptionMin = optimization?.AbsorptionMin ?? 2.5;
                    double absorptionMax = optimization?.AbsorptionMax ?? 3.5;
                    if (absorption < absorptionMin || absorption > absorptionMax)
                        return double.PositiveInfinity;
                    nodeAbsorptionDict[uniqueDeviceIds[i]] = absorption;
                }

                // Build working direction dictionary from sin/cos params
                var dirDict = new Dictionary<string, (double azRad, double elRad)>();
                for (int i = 0; i < directionalCount; i++)
                {
                    dirDict[directionalDeviceIds[i]] = (
                        Step2Layout.GetAzimuthRad(xa, i, absorptionCount, directionalCount),
                        Step2Layout.GetElevationRad(xa, i, absorptionCount, directionalCount)
                    );
                }

                // Regularization: sum of unit-circle penalties
                double penalty = 0.0;
                for (int i = 0; i < directionalCount; i++)
                    penalty += Step2Layout.UnitCirclePenalty(xa, i, absorptionCount, directionalCount);

                double error = CalculateErrorWithGain(allNodes, rxAdjRssiDict, nodeAbsorptionDict, dirDict);
                return error + penalty;
            },
            x => {
                var xa = x.ToArray();
                var nodeAbsorptionDict = new Dictionary<string, double>();
                for (int i = 0; i < absorptionCount; i++)
                    nodeAbsorptionDict[uniqueDeviceIds[i]] = xa[Step2Layout.AbsIndex(i, absorptionCount)];

                var dirDict = new Dictionary<string, (double azRad, double elRad)>();
                for (int i = 0; i < directionalCount; i++)
                {
                    dirDict[directionalDeviceIds[i]] = (
                        Step2Layout.GetAzimuthRad(xa, i, absorptionCount, directionalCount),
                        Step2Layout.GetElevationRad(xa, i, absorptionCount, directionalCount)
                    );
                }

                // Full 4N gradient via finite differences over all parameters
                var gradient = Vector<double>.Build.Dense(vecLen);
                double eps = 1e-5;

                double baseError = CalculateErrorWithGain(allNodes, rxAdjRssiDict, nodeAbsorptionDict, dirDict);
                double basePenalty = 0.0;
                for (int i = 0; i < directionalCount; i++)
                    basePenalty += Step2Layout.UnitCirclePenalty(xa, i, absorptionCount, directionalCount);
                double baseVal = baseError + basePenalty;

                for (int j = 0; j < vecLen; j++)
                {
                    var xPlus = xa.ToArray(); // copy
                    xPlus[j] += eps;

                    var absPlus = new Dictionary<string, double>(nodeAbsorptionDict);
                    for (int i = 0; i < absorptionCount; i++)
                        absPlus[uniqueDeviceIds[i]] = xPlus[Step2Layout.AbsIndex(i, absorptionCount)];

                    var dirPlus = new Dictionary<string, (double azRad, double elRad)>();
                    for (int i = 0; i < directionalCount; i++)
                    {
                        dirPlus[directionalDeviceIds[i]] = (
                            Step2Layout.GetAzimuthRad(xPlus, i, absorptionCount, directionalCount),
                            Step2Layout.GetElevationRad(xPlus, i, absorptionCount, directionalCount)
                        );
                    }

                    double penPlus = 0.0;
                    for (int i = 0; i < directionalCount; i++)
                        penPlus += Step2Layout.UnitCirclePenalty(xPlus, i, absorptionCount, directionalCount);

                    double errPlus = CalculateErrorWithGain(allNodes, rxAdjRssiDict, absPlus, dirPlus);
                    gradient[j] = (errPlus + penPlus - baseVal) / eps;
                }

                return gradient;
            });

        // -----------------------------------------------------------------------
        // Initial-guess vector (4N): abs block seeded from calibration, antenna
        // blocks seeded to boresight-pointing identity (azimuth=0, elevation=0).
        // -----------------------------------------------------------------------
        var initialGuessArr = new double[vecLen];
        for (int i = 0; i < absorptionCount; i++)
        {
            existingSettings.TryGetValue(uniqueDeviceIds[i], out var nodeSettings);
            double absorptionMin = optimization?.AbsorptionMin ?? 2.5;
            double absorptionMax = optimization?.AbsorptionMax ?? 3.5;
            // Abs block: clamp existing calibration value within global bounds
            initialGuessArr[Step2Layout.AbsIndex(i, absorptionCount)] = Math.Clamp(
                nodeSettings?.Calibration?.Absorption ?? (absorptionMax - absorptionMin) / 2 + absorptionMin,
                absorptionMin, absorptionMax);
        }
        for (int i = 0; i < directionalCount; i++)
        {
            existingSettings.TryGetValue(directionalDeviceIds[i], out var nodeSettings);
            double azDeg = nodeSettings?.Calibration?.Azimuth ?? 0.0;
            double elDeg = nodeSettings?.Calibration?.Elevation ?? 0.0; // Default horizontal for optimizer (node-to-node paths)
            double azRad = azDeg * Math.PI / 180.0;
            double elRad = elDeg * Math.PI / 180.0;
            initialGuessArr[Step2Layout.SinAzIndex(i, absorptionCount, directionalCount)] = Math.Sin(azRad);
            initialGuessArr[Step2Layout.CosAzIndex(i, absorptionCount, directionalCount)] = Math.Cos(azRad);
            initialGuessArr[Step2Layout.SinElIndex(i, absorptionCount, directionalCount)] = Math.Sin(elRad);
        }
        var initialGuess = Vector<double>.Build.DenseOfArray(initialGuessArr);

        // -----------------------------------------------------------------------
        // Lower / upper bound vectors (4N) wired to Step2Layout offsets.
        // Abs block: global absorption range.
        // sinAz/cosAz: loose [-2, 2] — regularisation pulls them to the unit circle.
        // sinEl: strict [-1, 1] — sin(elevation) is always in this range.
        // -----------------------------------------------------------------------
        var lowerBoundArr = new double[vecLen];
        var upperBoundArr = new double[vecLen];
        for (int i = 0; i < absorptionCount; i++)
        {
            double absorptionMin = optimization?.AbsorptionMin ?? 2.5;
            double absorptionMax = optimization?.AbsorptionMax ?? 3.5;
            lowerBoundArr[Step2Layout.AbsIndex(i, absorptionCount)] = absorptionMin;
            upperBoundArr[Step2Layout.AbsIndex(i, absorptionCount)] = absorptionMax;
        }
        for (int i = 0; i < directionalCount; i++)
        {
            lowerBoundArr[Step2Layout.SinAzIndex(i, absorptionCount, directionalCount)] = -2.0;
            upperBoundArr[Step2Layout.SinAzIndex(i, absorptionCount, directionalCount)] =  2.0;
            lowerBoundArr[Step2Layout.CosAzIndex(i, absorptionCount, directionalCount)] = -2.0;
            upperBoundArr[Step2Layout.CosAzIndex(i, absorptionCount, directionalCount)] =  2.0;
            lowerBoundArr[Step2Layout.SinElIndex(i, absorptionCount, directionalCount)] = -1.0;
            upperBoundArr[Step2Layout.SinElIndex(i, absorptionCount, directionalCount)] =  1.0;
        }
        var lowerBound = Vector<double>.Build.DenseOfArray(lowerBoundArr);
        var upperBound = Vector<double>.Build.DenseOfArray(upperBoundArr);

        var solver = new BfgsBMinimizer(1e-7, 1e-7, 1e-7, 10000);
        var result = solver.FindMinimum(obj, lowerBound, upperBound, initialGuess);

        var minPoint = result.MinimizingPoint.ToArray();
        var nodeAbsorptions = new NodeAbsorptionMap();
        for (int i = 0; i < absorptionCount; i++)
            nodeAbsorptions[uniqueDeviceIds[i]] = minPoint[Step2Layout.AbsIndex(i, absorptionCount)];

        var nodeAzimuths = new NodeAzimuthMap();
        var nodeElevations = new NodeElevationMap();
        for (int i = 0; i < directionalCount; i++)
        {
            double azRad = Step2Layout.GetAzimuthRad(minPoint, i, absorptionCount, directionalCount);
            double elRad = Step2Layout.GetElevationRad(minPoint, i, absorptionCount, directionalCount);
            double azDeg = azRad * 180.0 / Math.PI;
            if (azDeg < 0) azDeg += 360.0;
            double elDeg = elRad * 180.0 / Math.PI;
            nodeAzimuths[directionalDeviceIds[i]] = azDeg;
            nodeElevations[directionalDeviceIds[i]] = elDeg;
        }
        return (Absorptions: nodeAbsorptions, Azimuths: nodeAzimuths, Elevations: nodeElevations, Error: result.FunctionInfoAtMinimum.Value);
    }

    private double CalculateError(List<Measure> nodes, Dictionary<string, double> rxAdjRssiDict,
        Dictionary<string, double>? nodeAbsorptionDict = null, Dictionary<(string, string), double>? pathAbsorptionDict = null)
    {
        return nodes.Select(n =>
        {
            var distance = n.Rx.Location.DistanceTo(n.Tx.Location);
            var rxAdjRssi = rxAdjRssiDict[n.Rx.Id];
            var txAdjRssi = rxAdjRssiDict[n.Tx.Id];
            double absorption;

            if (pathAbsorptionDict != null)
            {
                var pathKey = (Min(n.Rx.Id, n.Tx.Id), Max(n.Rx.Id, n.Tx.Id));
                absorption = pathAbsorptionDict[pathKey];
            }
            else if (nodeAbsorptionDict != null)
            {
                absorption = (nodeAbsorptionDict[n.Rx.Id] + nodeAbsorptionDict[n.Tx.Id]) / 2;
            }
            else
            {
                throw new ArgumentException("Either nodeAbsorptionDict or pathAbsorptionDict must be provided");
            }

            var calculatedDistance = Math.Pow(10, (-59 + rxAdjRssi + txAdjRssi - n.Rssi) / (10.0d * absorption));
            return Math.Pow(distance - calculatedDistance, 2);
        }).Average();
    }

    private double CalculateErrorWithGain(List<Measure> nodes, Dictionary<string, double> rxAdjRssiDict,
        Dictionary<string, double> nodeAbsorptionDict, Dictionary<string, (double azRad, double elRad)> dirDict)
    {
        return nodes.Select(n =>
        {
            var distance = n.Rx.Location.DistanceTo(n.Tx.Location);
            var rxAdjRssi = rxAdjRssiDict[n.Rx.Id];
            var txAdjRssi = rxAdjRssiDict[n.Tx.Id];
            var absorption = (nodeAbsorptionDict[n.Rx.Id] + nodeAbsorptionDict[n.Tx.Id]) / 2;

            double gainDb = 0.0;
            if (n.Rx.IsNode && n.Rx.HasDirectionalAntenna && dirDict.TryGetValue(n.Rx.Id, out var rxDir))
                gainDb = ComputeGainDb(n.Rx, n.Tx, rxDir.azRad, rxDir.elRad);

            var calculatedDistance = Math.Pow(10, (-59 + rxAdjRssi + gainDb + txAdjRssi - n.Rssi) / (10.0d * absorption));
            return Math.Pow(distance - calculatedDistance, 2);
        }).Average();
    }

    private static double ComputeGainDb(OptNode rxNode, OptNode txNode, double azRad, double elRad)
    {
        double px = Math.Sin(azRad) * Math.Cos(elRad);
        double py = Math.Cos(azRad) * Math.Cos(elRad);
        double pz = Math.Sin(elRad);

        return MathUtils.ComputeGainDb(px, py, pz,
            txNode.Location.X - rxNode.Location.X,
            txNode.Location.Y - rxNode.Location.Y,
            txNode.Location.Z - rxNode.Location.Z,
            10.0 * Math.Log10(rxNode.GMax), rxNode.PatternExponent, rxNode.BackLossDb);
    }

    private static string Min(string a, string b) => string.Compare(a, b) < 0 ? a : b;
    private static string Max(string a, string b) => string.Compare(a, b) >= 0 ? a : b;
}
