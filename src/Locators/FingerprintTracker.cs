using ESPresense.Extensions;
using ESPresense.Models;
using MathNet.Spatial.Euclidean;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ESPresense.Locators;

/// <summary>
/// A grid filter over the fingerprint surface. Where <see cref="FingerprintLocator"/> places each reading
/// on its own, the tracker carries a belief about where the device is from one report to the next, so
/// every report refines the fix instead of replacing it. On the robot survey a single reading located to
/// about 1.2 m and a 90 s dwell to 0.69 m; the filter is what gets the dwell's averaging while the device
/// keeps moving, with a report or two of latency.
///
/// The belief is a probability per lattice point of the surface, so it is exact: no particles, no
/// sampling noise, no resampling. The lattice only exists where the survey had support, so it doubles as
/// the map of where a device can be, with no wall or door model needed. Between reports the belief
/// diffuses by a Gaussian whose width grows with the time since the last report, scaled by how fast the
/// fix has actually been moving: a device that is still is not spread as if it might have run, and so
/// converges; one that is walking is followed. Carrying the belief along the fix's velocity was tried and
/// made things worse: the jitter of the fix became a spurious velocity.
///
/// What it buys, measured on the robot survey: standing still, three rounds of reports through the
/// tracker locate as well as the plain locator's windowed mean of the same reports, within the noise
/// between runs; on the move, heard a few nodes at a time, it beats averaging stale reports clearly and
/// trails matching the latest reports by about 0.2 m, because it carries the evidence from where the
/// device was a moment ago. Its value is a fix that does not jump, a confidence that means something,
/// and a room vote over the whole belief, not a lower error than the plain match on a surveyed home.
/// It is off by default.
///
/// Each new report multiplies every point's belief by the likelihood of that node's raw rssi there, in
/// units of the node's surveyed spread. Only reports that arrived since the last pass count, so a report
/// is never weighed twice. A node the map knows that the device does not hear counts as a reading at the
/// hearing floor wherever the surface expects it to be heard, as it does in the plain match (where it is
/// what fixes the worst errors); its evidence accrues at the rate the heard nodes report, so a pass with
/// three fresh reports out of nine heard nodes weighs each silent node a third.
///
/// The device's dB offset from the survey beacon (its transmit power, its antenna, the pocket it is in)
/// is profiled per lattice point from the latest report of every node currently heard, exactly as the
/// offset-free match does, and applied to the fresh reports. Three other schemes were tried and failed on
/// the robot survey: one shared offset carried between updates, seeded a dB wrong by a noisy first
/// reading, biased every update towards points where the whole field is weaker and slid the belief off
/// the survey; an offset profiled from a window of past reports was taken from where a moving device
/// used to be; and a per-point offset anchored by a decaying prior from earlier fixes fed the fix's own
/// error back into the likelihood. The fix is the belief-weighted mean of the points within a metre of
/// the peak; the spread reported for confidence covers every point, so a split belief reads as unsure.
/// </summary>
public class FingerprintTracker(Device device, Floor floor, FingerprintMap map, FingerprintConfig config,
    Func<string, Point3D?>? nodeLocation = null, int version = 0, Func<DateTime>? clock = null,
    ILogger<FingerprintTracker>? logger = null) : ILocate
{
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);
    /// <summary>The fix is taken from the belief within this distance (m) of its peak.</summary>
    private const double ClusterRadius = 1.0;
    /// <summary>
    /// Diffusion per update is the recent speed of the fix times the elapsed time, never less than half a
    /// lattice step (so a converged belief keeps its neighbours alive and can start moving again) and never
    /// more than a metre. A fix that has not moved is assumed able to move at a tenth of the configured speed.
    /// </summary>
    private const double MaxMotionPerUpdate = 1.0, StillFraction = 0.1, SpeedSmoothing = 0.3;
    private const double OffsetAdaptation = 0.2;
    /// <summary>Smallest diffusion, as a fraction of a lattice step: enough to keep a converged belief's neighbours alive.</summary>
    private const double MinDiffusionSteps = 0.25;
    /// <summary>Belief below this is not diffused onward: it saves most of the work and loses nothing a report would not.</summary>
    private const double NegligibleBelief = 1e-7;

    private FingerprintSurface? _surface;
    private double[] _belief = Array.Empty<double>();
    private Room?[] _roomOf = Array.Empty<Room?>();
    private bool[] _roomKnown = Array.Empty<bool>();
    private readonly Dictionary<string, DateTime> _consumed = new(StringComparer.OrdinalIgnoreCase);
    private DateTime? _lastUpdate;
    private bool _seeded;
    private Point3D? _lastFix;
    private double _recentSpeed;

    /// <summary>The device's dB offset from the survey beacon as seen at the fix (smoothed); NaN until the first fix.</summary>
    public double Offset { get; private set; } = double.NaN;
    /// <summary>Rms distance (m) of the belief from the fix: how sure the filter is.</summary>
    public double Spread { get; private set; } = double.NaN;
    /// <summary>How many lattice points the belief is effectively spread over (1/Σb²).</summary>
    public double EffectivePoints { get; private set; }
    public int Points => _belief.Length;

    public bool Locate(Scenario scenario)
    {
        var tracker = config.Tracker;
        var surface = map.Surface(floor.Id, config.Bandwidth, config.MissingRssi ?? -100, nodeLocation, version, config.SigmaFloor);
        if (surface.Count == 0)
        {
            scenario.Confidence = 0;
            return false;
        }
        if (!ReferenceEquals(surface, _surface)) Reset(surface);

        var now = _clock();
        var current = device.Nodes.Values
            .Where(n => n.Current && n.Node != null && n.LastHit != null && surface.NodeIndex(n.Node.Id) >= 0)
            .ToArray();
        var reading = current.ToDictionary(n => n.Node.Id, n => n.RawRssi, StringComparer.OrdinalIgnoreCase);
        var r = surface.Prepare(reading);

        var fresh = new bool[surface.Nodes.Count];
        var freshCount = 0;
        foreach (var n in current)
        {
            if (_consumed.TryGetValue(n.Node.Id, out var seen) && n.LastHit!.Value <= seen) continue;
            fresh[surface.NodeIndex(n.Node.Id)] = true;
            freshCount++;
        }
        if (freshCount == 0) return false;
        foreach (var n in current) _consumed[n.Node.Id] = n.LastHit!.Value;

        // The per-point offset needs more than one node to say anything; until then there is nothing to place.
        if (!_seeded && r.Shared < Math.Max(2, config.MinCommon)) { scenario.Confidence = 0; return false; }

        try
        {
            var dt = _lastUpdate is { } last ? Math.Clamp((now - last).TotalSeconds, 0.05, 10) : 0;
            var logB = new double[surface.Count];
            double temper, silentScale;
            if (!_seeded)
            {
                // A first reading is correlated with nothing yet, so it is not tempered; silence counts in full, as in the plain match.
                temper = 1.0;
                silentScale = tracker.SilentWeight;
                _seeded = true;
            }
            else
            {
                var speed = Math.Max(StillFraction * tracker.Speed, _recentSpeed);
                Diffuse(surface, Math.Clamp(speed * dt, surface.Step * MinDiffusionSteps, MaxMotionPerUpdate));
                for (var i = 0; i < logB.Length; i++)
                    logB[i] = _belief[i] > 0 ? Math.Log(_belief[i]) : double.NegativeInfinity;
                temper = tracker.Temper;
                silentScale = tracker.SilentWeight * freshCount / Math.Max(1, r.Shared);
            }
            var offsets = new double[logB.Length];
            for (var i = 0; i < logB.Length; i++)
            {
                if (!double.IsFinite(logB[i])) continue;
                offsets[i] = OffsetAt(surface, i, r);
                logB[i] += surface.LogLikelihood(i, r, fresh, offsets[i], temper);
            }
            if (silentScale > 0)
                for (var k = 0; k < surface.Nodes.Count; k++)
                {
                    if (r.Has[k]) continue;
                    for (var i = 0; i < logB.Length; i++)
                        if (double.IsFinite(logB[i]))
                            logB[i] += silentScale * surface.LogLikelihoodSilent(i, k, offsets[i], temper);
                }

            var max = logB.Max();
            if (!double.IsFinite(max))
            {
                Reset(surface);
                scenario.Confidence = 0;
                return false;
            }
            double sum = 0;
            for (var i = 0; i < logB.Length; i++) { _belief[i] = double.IsFinite(logB[i]) ? Math.Exp(logB[i] - max) : 0; sum += _belief[i]; }
            double sumSq = 0; var peak = 0;
            for (var i = 0; i < _belief.Length; i++)
            {
                _belief[i] /= sum; sumSq += _belief[i] * _belief[i];
                if (_belief[i] > _belief[peak]) peak = i;
            }
            EffectivePoints = 1 / sumSq;

            var mean = Estimate(surface, peak);
            double spread = 0;
            for (var i = 0; i < _belief.Length; i++)
            {
                if (_belief[i] < NegligibleBelief) continue;
                var d = surface.Point(i).DistanceTo(mean);
                spread += _belief[i] * d * d;
            }
            Spread = Math.Sqrt(spread);

            if (_lastFix is { } previous && dt > 0)
                _recentSpeed += SpeedSmoothing * (previous.DistanceTo(mean) / dt - _recentSpeed);
            _lastFix = mean;

            var at = surface.Nearest(mean.X, mean.Y, ClusterRadius);
            if (at >= 0 && r.Shared >= 2)
            {
                var observed = surface.Offset(at, r);
                if (!double.IsNaN(observed)) Offset = double.IsNaN(Offset) ? observed : Offset + OffsetAdaptation * (observed - Offset);
            }

            var room = RoomVote(surface, floor);
            _lastUpdate = now;

            scenario.Minimum = current.Min(n => (double?)n.Distance);
            scenario.Fixes = current.Length;
            scenario.Floor = floor;
            scenario.UpdateLocation(mean);
            var (_, rmsDb, _) = at >= 0 ? surface.Evaluate(at, r) : (0, double.NaN, 0);
            scenario.Error = double.IsNaN(rmsDb) ? null : rmsDb;
            scenario.PearsonCorrelation = null;
            // A tight belief makes a confident fix; a reading that fits the surface badly where it sits
            // (above the 6 dB a spot a few metres away typically scores) takes it down.
            var confidence = 100 / (1 + Spread * Spread) - 2 * Math.Max(0, (double.IsNaN(rmsDb) ? 0 : rmsDb) - 6);
            scenario.Confidence = (int)Math.Clamp(confidence, 1, 100);
            scenario.Room = room;
        }
        catch (Exception ex)
        {
            scenario.Confidence = 0;
            (logger ?? NullLogger<FingerprintTracker>.Instance).LogError(ex, "Fingerprint tracker error for {Device}", device);
            return false;
        }

        return scenario.Location.DistanceTo(scenario.LastLocation) >= 0.1;
    }

    /// <summary>The offset that best explains the current reading at lattice point <paramref name="i"/>; 0 when no heard node is predicted there.</summary>
    private static double OffsetAt(FingerprintSurface surface, int i, FingerprintSurface.Reading r)
    {
        var off = surface.Offset(i, r);
        return double.IsNaN(off) ? 0 : off;
    }

    private void Reset(FingerprintSurface surface)
    {
        _surface = surface;
        _belief = new double[surface.Count];
        _roomOf = new Room?[surface.Count];
        _roomKnown = new bool[surface.Count];
        _lastUpdate = null;
        _seeded = false;
        _lastFix = null;
        _recentSpeed = 0;
        _consumed.Clear();
    }

    /// <summary>Spreads the belief over the lattice by a Gaussian of <paramref name="sigma"/>, landing only on points that exist.</summary>
    private void Diffuse(FingerprintSurface surface, double sigma)
    {
        var next = new double[_belief.Length];
        var reach = (int)Math.Ceiling(3 * sigma / surface.Step);
        var twoS2 = 2 * sigma * sigma;
        var targets = new List<(int Index, double Weight)>((2 * reach + 1) * (2 * reach + 1));
        for (var j = 0; j < _belief.Length; j++)
        {
            var b = _belief[j];
            if (b < NegligibleBelief) continue;
            var (ix, iy) = surface.Cell(j);
            var from = surface.Point(j);
            targets.Clear();
            double norm = 0;
            for (var dx = -reach; dx <= reach; dx++)
                for (var dy = -reach; dy <= reach; dy++)
                {
                    var i = surface.At(ix + dx, iy + dy);
                    if (i < 0) continue;
                    var p = surface.Point(i);
                    var d2 = (p.X - from.X) * (p.X - from.X) + (p.Y - from.Y) * (p.Y - from.Y);
                    var w = Math.Exp(-d2 / twoS2);
                    targets.Add((i, w)); norm += w;
                }
            foreach (var (i, w) in targets) next[i] += b * w / norm;
        }
        _belief = next;
    }

    /// <summary>The belief-weighted mean of the lattice points within <see cref="ClusterRadius"/> of the peak.</summary>
    private Point3D Estimate(FingerprintSurface surface, int peak)
    {
        var centre = surface.Point(peak);
        double mx = 0, my = 0, mz = 0, ws = 0;
        for (var i = 0; i < _belief.Length; i++)
        {
            if (_belief[i] < NegligibleBelief) continue;
            var p = surface.Point(i);
            if (p.DistanceTo(centre) > ClusterRadius) continue;
            mx += _belief[i] * p.X; my += _belief[i] * p.Y; mz += _belief[i] * p.Z; ws += _belief[i];
        }
        return ws > 0 ? new Point3D(mx / ws, my / ws, mz / ws) : centre;
    }

    private Room? RoomVote(FingerprintSurface surface, Floor floor)
    {
        var votes = new Dictionary<Room, double>();
        for (var i = 0; i < _belief.Length; i++)
        {
            if (_belief[i] < NegligibleBelief) continue;
            if (!_roomKnown[i])
            {
                var p = surface.Point(i).ToPoint2D();
                _roomOf[i] = floor.Rooms.Values.FirstOrDefault(room => room.Polygon?.EnclosesPoint(p) ?? false);
                _roomKnown[i] = true;
            }
            if (_roomOf[i] is { } r) votes[r] = votes.GetValueOrDefault(r) + _belief[i];
        }
        return votes.Count > 0 ? votes.MaxBy(v => v.Value).Key : null;
    }
}
