using ESPresense.Models;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Optimizers;

/// <summary>
/// Self-calibration from located devices' residuals (#1860): per-node rxAdj and trust, per-device rssi@1m.
///
/// Node positions are the ground truth. A device window heard by a dozen nodes has three unknowns; when the
/// calibration is right the distance spheres meet at a point, and when a node is biased its sphere is too
/// small (or large) for every device from every direction. So each cycle alternates:
///
///   1. Solve every window with the current calibration (<see cref="DeviceGridSolver"/>), with every node
///      and leaving each node out in turn. The leave-one-out fit matters: a node's own bias drags the
///      full fit toward it and hides itself.
///   2. Per node, bias = mobility-weighted mean of its leave-one-out residuals (clipped to +/-15 dB), and
///      trust = their variance, used as solver weight 1 / (variance + 4) in the next solve.
///      Per device, offset = its mean full-fit residual across nodes.
///   3. Damped update (0.7): rxAdj += bias, re-centred to zero mean change, so the absolute level goes to
///      the devices' rssi@1m; rssi@1m += offset.
///
/// Identifiability: a node's bias is what is common to it across devices and places; a device's offset is
/// what is common to it across nodes; the overall level is pinned by the metre-scale node positions (spheres
/// of the wrong overall size do not meet). A device that sits still cannot separate a node's offset from its
/// own position, so each device is weighted by how much it moves, and a node only moves on enough windows
/// from devices that do.
///
/// The firmware has one rxAdj per node, applied to node-to-node links too, so the device-facing value makes
/// some node-to-node links look worse. That is accepted: locating devices is the goal. The runner scores
/// proposals with a device-residual term for the same reason (see <see cref="OptimizationRunner"/>).
///
/// The learned rssi@1m values carry over between cycles; the runner writes one to device settings only once
/// the device has enough windows, moves, and the value has held still for two cycles.
/// </summary>
public class DeviceResidualOptimizer : IOptimizer
{
    internal const int Alternations = 3;
    internal const double Damping = 0.7;
    internal const double ResidualClip = 15;
    internal const double VarianceFloor = 4;
    internal const double DefaultVariance = 25;

    /// <summary>Most rxAdj change proposed for a node in one cycle; the rest follows next cycle.</summary>
    internal const double MaxNodeStepDb = 5;

    /// <summary>A node's offset is only estimated from at least this many windows...</summary>
    internal const int MinNodeWindows = 20;

    /// <summary>...and at least this much weight from devices that move (mobility of at least <see cref="MovingMobility"/>).</summary>
    internal const double MinMovingWeight = 10;

    /// <summary>Mobility at which a device counts as moving: for node evidence, and for its rssi@1m to be applied.</summary>
    internal const double MovingMobility = 0.3;

    /// <summary>A learned rssi@1m that moved less than this between cycles counts as stable.</summary>
    internal const double StableDb = 1;

    /// <summary>Windows solved per cycle; above this the history is thinned evenly in time.</summary>
    internal const int MaxTrainingWindows = 20_000;

    /// <summary>Bounds on a device's estimated sensitivity (see <see cref="StepDevices"/>), and the most its rssi@1m moves per alternation.</summary>
    internal const double MinSensitivity = 0.2;
    internal const double MaxDeviceStepDb = 6;

    private readonly State _state;
    private readonly ILogger<DeviceResidualOptimizer> _logger;
    private readonly Dictionary<string, double> _deviceRef = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _stableCycles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _nodeVariance = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _deviceSensitivity = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Guards the dictionaries above, which a dry run copies (<see cref="Fork"/>) while a live cycle may be running.</summary>
    private readonly object _stateLock = new();

    public DeviceResidualOptimizer(State state, ILogger<DeviceResidualOptimizer> logger)
    {
        _state = state;
        _logger = logger;
    }

    public string Name => "Device Residual";

    /// <summary>
    /// A new optimizer that starts from a copy of what this one has learned across cycles (each device's rssi@1m,
    /// how long it has held, per-node trust), for a dry run that must neither share state with nor advance the
    /// live optimizer.
    /// </summary>
    public DeviceResidualOptimizer Fork()
    {
        var fork = new DeviceResidualOptimizer(_state, _logger);
        lock (_stateLock)
        {
            foreach (var (k, v) in _deviceRef) fork._deviceRef[k] = v;
            foreach (var (k, v) in _stableCycles) fork._stableCycles[k] = v;
            foreach (var (k, v) in _nodeVariance) fork._nodeVariance[k] = v;
            foreach (var (k, v) in _deviceSensitivity) fork._deviceSensitivity[k] = v;
        }
        return fork;
    }

    public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings)
    {
        var or = new OptimizationResults();
        var optimization = _state.Config?.Optimization;
        if (optimization == null) return or;
        var settings = optimization.DeviceResidual;

        if (os.Geometry == null || os.DeviceWindows.Count == 0)
        {
            _logger.LogInformation("Device residual: no device windows yet (each needs {MinNodes} positioned nodes hearing a located device)", settings.EffectiveMinNodes);
            return or;
        }

        var solver = DeviceGridSolver.For(os.Geometry);
        var devices = new List<string>();
        var deviceIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int DeviceIndex(string id)
        {
            if (!deviceIndex.TryGetValue(id, out var i))
            {
                i = devices.Count;
                devices.Add(id);
                deviceIndex[id] = i;
            }
            return i;
        }

        var all = DeviceResidualModel.Prepare(os.DeviceWindows.OrderBy(w => w.End).Select(w => (w, 0)), solver, settings.EffectiveMinNodes, DeviceIndex);
        if (all.Count == 0)
        {
            _logger.LogInformation("Device residual: none of {Windows} device windows has {MinNodes} positioned nodes", os.DeviceWindows.Count, settings.EffectiveMinNodes);
            return or;
        }

        int nodeCount = solver.NodeIds.Count, deviceCount = devices.Count;
        var windowCounts = new int[deviceCount];
        var latestRef = new double[deviceCount];
        foreach (var w in all)
        {
            windowCounts[w.Device]++;
            latestRef[w.Device] = w.Source.RefRssi; // ordered by time, so the last one wins
        }
        var training = Thin(all, MaxTrainingWindows);

        var start = new double[nodeCount];
        var absorption = new double[nodeCount];
        var weights = new double[nodeCount];
        var reference = new double[deviceCount];
        var sensitivity = new double[deviceCount];
        lock (_stateLock)
        {
            for (int n = 0; n < nodeCount; n++)
            {
                var id = solver.NodeIds[n];
                existingSettings.TryGetValue(id, out var ns);
                start[n] = ns?.Calibration?.RxAdjRssi ?? 0;
                absorption[n] = ns?.Calibration?.Absorption ?? DeviceResidualModel.DefaultAbsorption;
                weights[n] = 1 / ((_nodeVariance.TryGetValue(id, out var v) ? v : DefaultVariance) + VarianceFloor);
            }
            for (int d = 0; d < deviceCount; d++)
            {
                reference[d] = _deviceRef.TryGetValue(devices[d], out var learned) ? learned : latestRef[d];
                sensitivity[d] = _deviceSensitivity.GetValueOrDefault(devices[d], 1.0);
            }
        }
        var rx = (double[])start.Clone();
        var previousResidual = Enumerable.Repeat(double.NaN, deviceCount).ToArray();
        var previousStep = new double[deviceCount];

        var stats = new NodeStats(nodeCount);
        var mobility = new double[deviceCount];
        var moved = new bool[nodeCount];
        double before = double.NaN, scaleBefore = double.NaN;

        for (int it = 0; it < Alternations; it++)
        {
            var pass = DeviceResidualModel.Run(solver, training, rx, absorption, w => reference[w.Device], weights, leaveOneOut: true);
            if (it == 0) (before, scaleBefore) = (MeanAbs(pass), MeanScale(pass));

            Mobility(training, pass, mobility);
            stats = NodeStats.From(training, pass, mobility, nodeCount);

            // Node step: the damped bias, re-centred so the absolute level stays with the devices.
            var step = new double[nodeCount];
            var movable = 0;
            double mean = 0;
            for (int n = 0; n < nodeCount; n++)
            {
                if (!stats.CanMove(n)) continue;
                step[n] = Damping * stats.Bias[n];
                mean += step[n];
                movable++;
            }
            if (movable > 0) mean /= movable;
            for (int n = 0; n < nodeCount; n++)
            {
                if (!stats.CanMove(n)) continue;
                rx[n] += step[n] - mean;
                moved[n] = true;
            }

            StepDevices(training, pass, reference, sensitivity, previousResidual, previousStep);

            for (int n = 0; n < nodeCount; n++)
                weights[n] = 1 / ((stats.Windows[n] >= MinNodeWindows ? stats.Variance[n] : DefaultVariance) + VarianceFloor);
        }

        // Cap the cycle's change per node and keep within the configured limits.
        for (int n = 0; n < nodeCount; n++)
        {
            if (!moved[n]) continue;
            var id = solver.NodeIds[n];
            var wanted = rx[n] - start[n];
            var change = Math.Clamp(wanted, -MaxNodeStepDb, MaxNodeStepDb);
            if (change != wanted)
                _logger.LogInformation("Device residual: {NodeId,-20} rxAdj change {Wanted:+0.0;-0.0} dB capped at {Change:+0.0;-0.0} dB this cycle",
                    id, wanted, change);
            var value = start[n] + change;
            var limited = Math.Clamp(value, optimization.RxAdjRssiMin, optimization.RxAdjRssiMax);
            if (limited != value)
                _logger.LogInformation("Device residual: {NodeId,-20} rxAdj {Wanted:0.0} held at the rx_adj_rssi limit {Limit:0.0}",
                    id, value, limited);
            rx[n] = limited;
        }

        var final = DeviceResidualModel.Run(solver, training, rx, absorption, w => reference[w.Device], weights, leaveOneOut: true);
        var after = MeanAbs(final);
        var scaleAfter = MeanScale(final);

        var summary = new DeviceResidualState
        {
            UpdatedAt = DateTime.UtcNow,
            Windows = training.Count,
            Devices = deviceCount,
            MovingDevices = mobility.Count(m => m >= MovingMobility),
            ResidualBefore = before,
            ResidualAfter = after,
            ScaleBefore = scaleBefore,
            ScaleAfter = scaleAfter,
        };

        // What carries over to the next cycle; a dry run may be copying it (Fork) right now.
        lock (_stateLock)
        {
            for (int n = 0; n < nodeCount; n++)
            {
                if (stats.Windows[n] == 0) continue;
                var id = solver.NodeIds[n];
                var variance = stats.Windows[n] >= MinNodeWindows ? stats.Variance[n] : DefaultVariance;
                summary.Nodes[id] = new DeviceResidualNodeState
                {
                    RxAdjChange = moved[n] ? rx[n] - start[n] : null,
                    Variance = variance,
                    Weight = 1 / (variance + VarianceFloor),
                    Windows = stats.Windows[n],
                };
                if (stats.Windows[n] >= MinNodeWindows) _nodeVariance[id] = variance;
                if (!moved[n]) continue;
                or.Nodes[id] = new ProposedValues
                {
                    RxAdjRssi = rx[n],
                    Error = Math.Sqrt(stats.Variance[n] + stats.Bias[n] * stats.Bias[n]),
                    DeviceResidualVariance = variance,
                };
            }

            for (int d = 0; d < deviceCount; d++)
            {
                var id = devices[d];
                _deviceSensitivity[id] = sensitivity[d];
                var stable = _deviceRef.TryGetValue(id, out var previous) && Math.Abs(reference[d] - previous) < StableDb
                    ? _stableCycles.GetValueOrDefault(id) + 1
                    : 0;
                _deviceRef[id] = reference[d];
                _stableCycles[id] = stable;
                or.Devices[id] = new ProposedDeviceValues
                {
                    RefRssi = reference[d],
                    CurrentRefRssi = latestRef[d],
                    Windows = windowCounts[d],
                    Mobility = mobility[d],
                    StableCycles = stable,
                };
            }

            // Devices that have aged out of the history (random addresses come and go) are forgotten.
            foreach (var id in _deviceRef.Keys.Where(id => !deviceIndex.ContainsKey(id)).ToList())
            {
                _deviceRef.Remove(id);
                _stableCycles.Remove(id);
                _deviceSensitivity.Remove(id);
            }
        }

        // On the results, not on State: a dry run runs this too and must change nothing; the runner publishes it.
        or.DeviceResidual = summary;
        _logger.LogInformation("Device residual: {Windows} windows from {Devices} devices ({Moving} moving); mean |leave-one-out residual| {Before:0.00} dB -> {After:0.00} dB, solver scale {ScaleBefore:0.000} -> {ScaleAfter:0.000}; {Nodes} nodes moved",
            training.Count, deviceCount, summary.MovingDevices, before, after, scaleBefore, scaleAfter, or.Nodes.Count);
        return or;
    }

    /// <summary>
    /// Moves each device's rssi@1m by its mean full-fit residual: how much louder it is than its rssi@1m says.
    ///
    /// The residual understates the error. Each window's position is fitted too, and moving the device away
    /// from its nearest nodes explains most of a uniform error in its distances, so a device whose rssi@1m is
    /// 8 dB off may show a mean residual of only 2 dB (sensitivity 0.25). A plain damped step then takes a
    /// dozen alternations to get there. The sensitivity is measured instead, from how much the residual fell
    /// after the previous step (a secant), and the damped step is divided by it: the same fixed point (zero
    /// mean residual), reached in a few alternations. It is bounded below by <see cref="MinSensitivity"/>,
    /// only re-measured after a step large enough to measure it by, and each step is capped.
    /// </summary>
    private static void StepDevices(IReadOnlyList<PreparedWindow> windows, DeviceResidualPass pass, double[] reference,
        double[] sensitivity, double[] previousResidual, double[] previousStep)
    {
        var sum = new double[reference.Length];
        var count = new int[reference.Length];
        for (int i = 0; i < windows.Count; i++)
        {
            sum[windows[i].Device] += pass.MeanFullResidual[i];
            count[windows[i].Device]++;
        }
        for (int d = 0; d < reference.Length; d++)
        {
            if (count[d] == 0) continue;
            var residual = sum[d] / count[d];
            if (double.IsFinite(previousResidual[d]) && Math.Abs(previousStep[d]) >= 0.25)
                sensitivity[d] = Math.Clamp((previousResidual[d] - residual) / previousStep[d], MinSensitivity, 1.0);
            var step = Math.Clamp(Damping * residual / sensitivity[d], -MaxDeviceStepDb, MaxDeviceStepDb);
            reference[d] += step;
            previousResidual[d] = residual;
            previousStep[d] = step;
        }
    }

    /// <summary>Every k-th window so at most <paramref name="max"/> remain, evenly spread over the (time-ordered) history.</summary>
    private static List<PreparedWindow> Thin(List<PreparedWindow> windows, int max)
    {
        if (windows.Count <= max) return windows;
        var step = (double)windows.Count / max;
        return Enumerable.Range(0, max).Select(k => windows[(int)(k * step)]).ToList();
    }

    private static void Mobility(IReadOnlyList<PreparedWindow> windows, DeviceResidualPass pass, double[] mobility)
    {
        var positions = new List<Point3D>[mobility.Length];
        for (int i = 0; i < windows.Count; i++)
            (positions[windows[i].Device] ??= new List<Point3D>()).Add(pass.Positions[i]);
        for (int d = 0; d < mobility.Length; d++)
            mobility[d] = DeviceResidualModel.Mobility(positions[d] ?? new List<Point3D>());
    }

    internal static double MeanAbs(DeviceResidualPass pass)
    {
        double sum = 0;
        long count = 0;
        foreach (var residuals in pass.LooResidual!)
            foreach (var r in residuals)
            {
                sum += Math.Abs(r);
                count++;
            }
        return count > 0 ? sum / count : double.NaN;
    }

    internal static double MeanScale(DeviceResidualPass pass) =>
        pass.LogScale.Length > 0 ? pass.LogScale.Average(l => Math.Pow(10, l)) : double.NaN;

    /// <summary>Per node, mobility-weighted mean and variance of its clipped leave-one-out residuals.</summary>
    private sealed class NodeStats
    {
        public NodeStats(int nodes)
        {
            Bias = new double[nodes];
            Variance = new double[nodes];
            Windows = new int[nodes];
            MovingWeight = new double[nodes];
        }

        public double[] Bias { get; }
        public double[] Variance { get; }
        public int[] Windows { get; }
        public double[] MovingWeight { get; }

        public bool CanMove(int n) => Windows[n] >= MinNodeWindows && MovingWeight[n] >= MinMovingWeight;

        public static NodeStats From(IReadOnlyList<PreparedWindow> windows, DeviceResidualPass pass, double[] mobility, int nodes)
        {
            var stats = new NodeStats(nodes);
            var s0 = new double[nodes];
            var s1 = new double[nodes];
            var s2 = new double[nodes];
            for (int i = 0; i < windows.Count; i++)
            {
                var w = windows[i];
                var weight = mobility[w.Device];
                var loo = pass.LooResidual![i];
                for (int j = 0; j < w.Nodes.Length; j++)
                {
                    var n = w.Nodes[j];
                    var r = Math.Clamp(loo[j], -ResidualClip, ResidualClip);
                    s0[n] += weight;
                    s1[n] += weight * r;
                    s2[n] += weight * r * r;
                    stats.Windows[n]++;
                    if (weight >= MovingMobility) stats.MovingWeight[n] += weight;
                }
            }
            for (int n = 0; n < nodes; n++)
            {
                if (s0[n] <= 0) continue;
                stats.Bias[n] = s1[n] / s0[n];
                stats.Variance[n] = Math.Max(0, s2[n] / s0[n] - stats.Bias[n] * stats.Bias[n]);
            }
            return stats;
        }
    }
}
