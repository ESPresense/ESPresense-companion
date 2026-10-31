namespace ESPresense.Services.Occupancy;

/// <summary>
/// Coarse, device-free occupancy feature from WiFi CSI amplitude variance (esp-radar-style
/// presence detection — CMP-1, ESPA-197). This is a faithful C# port of the validated design in
/// FUSION_DESIGN.md / occupancy_fusion.py (tests/.../AccuracyHarness/Csi, PR #1708, stacked on
/// QA-1/ESPA-173): mean per-subcarrier amplitude variance *across time*, sqrt'd back to an
/// amplitude-like scale and normalized by a fixed reference ceiling. Deliberately does not
/// attempt pose/vitals/through-wall; that is out of scope per the ESPA-168 feasibility doc.
///
/// The threshold and reference ceiling are placeholders calibrated against ESPA-173's synthetic
/// ECF1 corpus (vacant clusters ~0.043-0.047, occupied-still ~0.117-0.131) and will not survive
/// contact with a real home — CMP-3 (ESPA-199) replaces the fixed global threshold with a
/// per-node adaptive baseline once FW-1 lands a measured corpus.
/// </summary>
public sealed class CsiOccupancyDetector
{
    /// <summary>v1 global placeholder threshold — see FUSION_DESIGN.md "Threshold, honestly".</summary>
    public const double OccupiedThreshold = 0.08;

    private const double ReferenceCeiling = 40.0;

    private readonly TimeSpan _window;
    private readonly Dictionary<string, List<(DateTimeOffset Ts, double[] Amplitudes)>> _buffers = new();

    public CsiOccupancyDetector(TimeSpan? window = null)
    {
        _window = window ?? TimeSpan.FromSeconds(2);
        if (_window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window), "window must be positive");
    }

    /// <summary>
    /// Feed one CSI frame's raw IQ payload (alternating int8 I/Q per subcarrier, per the
    /// firmware's binary CSI frame layout v1 / ECF1 REC_CSI payload) for a node, and get back
    /// that node's current rolling-window motion score.
    ///
    /// A node an orchestrator hasn't observed this cycle carries no signal at all — that absence,
    /// not a score of 0, is what should reach <see cref="OccupancyFusion"/> as a null CSI score,
    /// mirroring occupancy_fusion.py's "missing CSI reading returns None, never 0.0" rule (a
    /// missing reading is not evidence of an empty room).
    /// </summary>
    public CsiOccupancySignal Observe(string nodeId, ReadOnlySpan<sbyte> iq, DateTimeOffset ts)
    {
        if (iq.Length == 0 || iq.Length % 2 != 0)
            throw new ArgumentException("IQ payload must contain a nonzero, even number of I/Q bytes", nameof(iq));

        var subcarriers = iq.Length / 2;
        var amplitudes = new double[subcarriers];
        for (var i = 0; i < subcarriers; i++)
        {
            double re = iq[2 * i];
            double im = iq[2 * i + 1];
            amplitudes[i] = Math.Sqrt(re * re + im * im);
        }

        if (!_buffers.TryGetValue(nodeId, out var buffer))
        {
            buffer = new List<(DateTimeOffset, double[])>();
            _buffers[nodeId] = buffer;
        }

        buffer.Add((ts, amplitudes));
        // Evict by age against the latest timestamp SEEN SO FAR, not the just-arrived one: CSI
        // frames aren't guaranteed to arrive in order. A queue that only inspects its head can
        // leave an out-of-order stale frame buried mid-buffer forever (reported by CodeRabbit on
        // PR #1707). Anchoring on "just arrived" instead of "latest known" has the same hole in
        // reverse -- a late frame whose own timestamp is older than the window's true leading
        // edge would wrongly spare everything newer than itself. Anchoring on the max keeps the
        // window's leading edge monotonic regardless of arrival order.
        var latestTs = buffer.Max(f => f.Ts);
        buffer.RemoveAll(f => latestTs - f.Ts > _window);

        var frames = buffer.Select(f => f.Amplitudes).ToArray();
        var score = MotionScore(frames, subcarriers);

        return new CsiOccupancySignal(nodeId, ts, score, buffer.Count);
    }

    private static double MotionScore(double[][] frames, int subcarriers)
    {
        var nFrames = frames.Length;
        var meanVariance = 0.0;
        for (var k = 0; k < subcarriers; k++)
        {
            var mean = 0.0;
            for (var f = 0; f < nFrames; f++)
                mean += frames[f][k];
            mean /= nFrames;

            var variance = 0.0;
            for (var f = 0; f < nFrames; f++)
            {
                var d = frames[f][k] - mean;
                variance += d * d;
            }
            variance /= nFrames;

            meanVariance += variance;
        }
        meanVariance /= subcarriers;

        return Math.Min(1.0, Math.Sqrt(meanVariance) / ReferenceCeiling);
    }
}

/// <summary>A node's rolling-window CSI motion-score read (0..1, esp-radar-style energy feature).</summary>
public readonly record struct CsiOccupancySignal(string NodeId, DateTimeOffset Timestamp, double MotionScore, int WindowFrameCount);
