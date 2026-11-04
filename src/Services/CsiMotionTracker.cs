using ESPresense.Events;
using MathNet.Numerics.Statistics;

namespace ESPresense.Services;

/// <summary>A node's current passive-WiFi motion read-out: its raw reading, the adaptive baseline it is
/// being compared against, the ratio between them, and whether that ratio has persisted long enough to
/// count as motion rather than a one-window blip.</summary>
public readonly record struct NodeMotion(double Current, double Baseline, double Score, bool IsMotion);

/// <summary>
/// Tracks each node's <c>csi_variance</c> reports and turns them into a per-node motion signal, without
/// ever comparing against a fixed absolute level: real empty-room baselines span 10-34 across nodes in
/// the reference data, and one node's own empty-room level drifted ~23 to ~30 over a few hours.
/// </summary>
public sealed class CsiMotionTracker
{
    // Empty levels drift over hours, not seconds, so the baseline must average over a window long
    // enough to ride that drift out rather than chase it: a short window would relabel the drift itself
    // as motion, and the inverse (window too long) would make the baseline lag a real level change for
    // days. A low percentile over this window still rejects day-to-day motion episodes as long as they
    // occupy well under BaselinePercentile% of it, which holds for anything short of near-constant occupancy.
    internal static readonly TimeSpan BaselineWindow = TimeSpan.FromHours(4);

    // Below this many samples the window is too thin to trust a percentile (it would just echo whichever
    // reading happens to be smallest); fall back to a plain average until enough history accumulates.
    private const int MinBaselineSamples = 12;
    private const double BaselinePercentile = 10;

    // A relative multiplier, never an absolute CSI value -- see the class remarks.
    private const double MotionRatio = 2.0;

    // The Office false-alarm reference case spiked to 10x+ baseline for a single 5s window and then
    // reverted; the Office case that must still register spent two consecutive windows elevated. Requiring
    // 2 consecutive elevated windows tells those apart without a time-based debounce, which would have to
    // guess at a duration instead of just counting what already arrived.
    private const int ConsecutiveWindowsForMotion = 2;

    // A node that stops publishing csi_variance (offline, removed, firmware hang) must not leave its
    // room stuck "in motion" forever -- only Observe() clears _consecutiveElevated, and that only runs
    // when a new reading arrives. 30s (6 missed 5s reports) is a placeholder: wide enough to ride out a
    // couple of dropped packets, tight enough to clear a genuinely offline node promptly. Tune against
    // the real csi_variance cadence once it's observed in the field.
    private static readonly TimeSpan MotionInactivityTimeout = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<(DateTime At, double Value)>> _window = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _consecutiveElevated = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _clock;

    public CsiMotionTracker(IMqttCoordinator mqtt, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        mqtt.NodeCsiVarianceReceivedAsync += OnReading;
    }

    private Task OnReading(NodeCsiVarianceReceivedEventArgs e)
    {
        Observe(e.NodeId, e.Value, _clock.GetUtcNow().UtcDateTime);
        return Task.CompletedTask;
    }

    /// <summary>Records one node reading. Exposed internally so tests can drive the tracker with
    /// controlled timestamps instead of real wall-clock time.</summary>
    internal void Observe(string nodeId, double value, DateTime at)
    {
        lock (_gate)
        {
            if (!_window.TryGetValue(nodeId, out var q))
                _window[nodeId] = q = new Queue<(DateTime At, double Value)>();

            // A gap longer than the inactivity timeout means GetMotion already expired any motion this
            // node was reporting; without this, one elevated reading right after a long silence would
            // inherit a stale consecutive-elevated count from before the gap and report motion
            // immediately, bypassing the two-reading sustained requirement.
            if (q.Count > 0 && at - q.Last().At > MotionInactivityTimeout)
                _consecutiveElevated[nodeId] = 0;

            q.Enqueue((at, value));
            while (q.Count > 0 && at - q.Peek().At > BaselineWindow)
                q.Dequeue();

            var baseline = Baseline(q);
            var ratio = baseline > 0 ? value / baseline : 1.0;
            _consecutiveElevated[nodeId] = ratio >= MotionRatio ? _consecutiveElevated.GetValueOrDefault(nodeId) + 1 : 0;
        }
    }

    private static double Baseline(Queue<(DateTime At, double Value)> window)
    {
        if (window.Count == 0) return 0;
        if (window.Count < MinBaselineSamples) return window.Average(s => s.Value);
        return window.Select(s => s.Value).Percentile((int)BaselinePercentile);
    }

    /// <summary>
    /// The node's current motion read-out as of <paramref name="now"/>, or null if the node has never
    /// reported. <paramref name="now"/> is taken from the caller (ultimately <see cref="CsiFusionService.Tick"/>'s
    /// own deterministic clock) rather than read from wall-clock here, so a fixture or test driving the
    /// tracker with historical timestamps does not have every reading spuriously expire against the real
    /// current time.
    /// </summary>
    public NodeMotion? GetMotion(string nodeId, DateTime now)
    {
        lock (_gate)
        {
            if (!_window.TryGetValue(nodeId, out var q) || q.Count == 0) return null;
            var last = q.Last();
            var baseline = Baseline(q);
            var score = baseline > 0 ? last.Value / baseline : 1.0;
            var isMotion = _consecutiveElevated.GetValueOrDefault(nodeId) >= ConsecutiveWindowsForMotion
                && now - last.At <= MotionInactivityTimeout;
            return new NodeMotion(last.Value, baseline, score, isMotion);
        }
    }
}
