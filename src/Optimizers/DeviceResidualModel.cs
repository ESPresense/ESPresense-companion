using ESPresense.Models;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Optimizers;

/// <summary>A <see cref="DeviceWindow"/> resolved against a <see cref="DeviceGridSolver"/>'s node indices.</summary>
internal sealed class PreparedWindow
{
    public required DeviceWindow Source { get; init; }

    /// <summary>Index of the device in the caller's device list.</summary>
    public required int Device { get; init; }

    /// <summary>Caller-defined group, e.g. the snapshot the window came from.</summary>
    public required int Group { get; init; }

    public required int[] Nodes { get; init; }
    public required double[] Rssi { get; init; }

    /// <summary>The rxAdj each reading was received with; NaN when unknown (then it cannot be re-based).</summary>
    public required double[] RxAdj { get; init; }
}

/// <summary>Per-window results of one pass of <see cref="DeviceResidualModel.Run"/>.</summary>
internal sealed class DeviceResidualPass
{
    public required Point3D[] Positions { get; init; }

    /// <summary>Per window, the mean full-fit residual (dB) over its nodes: how much louder the device is than its rssi@1m says.</summary>
    public required double[] MeanFullResidual { get; init; }

    /// <summary>Per window, log10 of the solver scale: the weighted mean of log10(fitted / measured distance).</summary>
    public required double[] LogScale { get; init; }

    /// <summary>Per window and node (aligned with <see cref="PreparedWindow.Nodes"/>), the leave-one-out residual in dB; null when not computed.</summary>
    public required double[][]? LooResidual { get; init; }
}

/// <summary>
/// The model the device_residual optimizer and its scoring share. A node that hears a device publishes
/// rssi = txRef(device) + bias(node) - 10 a log10 d. Ranged with the device's rssi@1m and a candidate rxAdj,
/// its measured log10 distance is m = (rssi@1m - (rssi + rxAdj_received - rxAdj)) / (10 a). Residuals are in
/// dB at the fitted position: r = 10 a (log10 d_fit - m), positive when the node hears the device louder than
/// the fit expects (it is hot, or the device is louder than its rssi@1m).
/// </summary>
internal static class DeviceResidualModel
{
    /// <summary>Absorption assumed for a node with none configured, as <see cref="OptimizationResults.Evaluate(List{OptimizationSnapshot}, Func{string, NodeSettings}, bool)"/>.</summary>
    public const double DefaultAbsorption = 2.7;

    /// <summary>Resolves windows to node indices, dropping unpositioned nodes and windows left with fewer than <paramref name="minNodes"/>.</summary>
    public static List<PreparedWindow> Prepare(IEnumerable<(DeviceWindow Window, int Group)> windows, DeviceGridSolver solver, int minNodes, Func<string, int> deviceIndex)
    {
        var prepared = new List<PreparedWindow>();
        var nodes = new List<int>();
        var rssi = new List<double>();
        var rxAdj = new List<double>();
        foreach (var (window, group) in windows)
        {
            nodes.Clear(); rssi.Clear(); rxAdj.Clear();
            foreach (var reading in window.Readings)
            {
                var n = solver.IndexOf(reading.NodeId);
                if (n < 0 || nodes.Contains(n)) continue;
                nodes.Add(n);
                rssi.Add(reading.Rssi);
                rxAdj.Add(reading.RxAdj ?? double.NaN);
            }
            if (nodes.Count < minNodes || !double.IsFinite(window.RefRssi)) continue;
            prepared.Add(new PreparedWindow
            {
                Source = window,
                Device = deviceIndex(window.DeviceId),
                Group = group,
                Nodes = nodes.ToArray(),
                Rssi = rssi.ToArray(),
                RxAdj = rxAdj.ToArray(),
            });
        }
        return prepared;
    }

    /// <summary>
    /// Solves every window with the given calibration: per node index, <paramref name="rxAdj"/> and
    /// <paramref name="absorption"/>; per window, the device's rssi@1m from <paramref name="refRssi"/>;
    /// per node, solver weight <paramref name="weights"/>. With <paramref name="leaveOneOut"/>, also each
    /// node's residual from the fit made without it.
    /// </summary>
    public static DeviceResidualPass Run(DeviceGridSolver solver, IReadOnlyList<PreparedWindow> windows,
        double[] rxAdj, double[] absorption, Func<PreparedWindow, double> refRssi, double[] weights, bool leaveOneOut)
    {
        var count = windows.Count;
        var positions = new Point3D[count];
        var meanFull = new double[count];
        var logScale = new double[count];
        var loo = leaveOneOut ? new double[count][] : null;

        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };
        Parallel.For(0, count, options, solver.CreateScratch, (i, _, scratch) =>
        {
            var w = windows[i];
            var h = w.Nodes.Length;
            var m = new double[h];
            var wt = new double[h];
            var fullLogD = new double[h];
            var looLogD = leaveOneOut ? new double[h] : Array.Empty<double>();
            var reference = refRssi(w);
            for (int j = 0; j < h; j++)
            {
                var n = w.Nodes[j];
                var rssi = double.IsNaN(w.RxAdj[j]) ? w.Rssi[j] : w.Rssi[j] + w.RxAdj[j] - rxAdj[n];
                m[j] = (reference - rssi) / (10 * absorption[n]);
                wt[j] = weights[n];
            }

            positions[i] = solver.Fit(w.Nodes, m, wt, fullLogD, looLogD, scratch);

            double sumFull = 0, sumScale = 0, sumW = 0;
            for (int j = 0; j < h; j++)
            {
                var a = absorption[w.Nodes[j]];
                sumFull += 10 * a * (fullLogD[j] - m[j]);
                sumScale += wt[j] * (fullLogD[j] - m[j]);
                sumW += wt[j];
            }
            meanFull[i] = sumFull / h;
            logScale[i] = sumW > 0 ? sumScale / sumW : 0;

            if (loo != null)
            {
                var r = new double[h];
                for (int j = 0; j < h; j++)
                    r[j] = 10 * absorption[w.Nodes[j]] * (looLogD[j] - m[j]);
                loo[i] = r;
            }
            return scratch;
        }, _ => { });

        return new DeviceResidualPass { Positions = positions, MeanFullResidual = meanFull, LogScale = logScale, LooResidual = loo };
    }

    /// <summary>
    /// How much a device moves: the spread (sum of the x and y standard deviations) of its solved positions
    /// over 1.5 m, clipped to [0.05, 1]. A device that sits still cannot tell a node's offset from its own
    /// position, so its residuals teach the node terms little.
    ///
    /// The spread is net of fix noise. With a few dB of radio noise, fixes of a device that never moves still
    /// scatter by about half a metre, which alone would score it 0.5. Noise is estimated per axis from
    /// successive fixes (half the mean squared difference: successive fixes of a still device differ by noise
    /// only, while a moving device's spread grows far beyond its step) and its variance is taken off.
    /// <paramref name="positions"/> must be in time order.
    /// </summary>
    public static double Mobility(IReadOnlyList<Point3D> positions)
    {
        if (positions.Count < 3) return 0.05;
        static double Spread(IReadOnlyList<Point3D> ps, Func<Point3D, double> axis)
        {
            double mean = 0, successive = 0;
            for (int i = 0; i < ps.Count; i++)
            {
                mean += axis(ps[i]);
                if (i > 0) successive += Math.Pow(axis(ps[i]) - axis(ps[i - 1]), 2);
            }
            mean /= ps.Count;
            double variance = 0;
            foreach (var p in ps) variance += Math.Pow(axis(p) - mean, 2);
            variance /= ps.Count;
            var noise = successive / (ps.Count - 1) / 2;
            return Math.Sqrt(Math.Max(0, variance - noise));
        }
        var spread = Spread(positions, p => p.X) + Spread(positions, p => p.Y);
        return Math.Clamp(spread / 1.5, 0.05, 1.0);
    }
}
