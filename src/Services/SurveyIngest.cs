using ESPresense.Models;

namespace ESPresense.Services;

/// <summary>
/// Turns a robot's poses and the reports its beacon produces into fingerprints. Pure: feed it poses and
/// reports, call <see cref="Drain"/>, get fingerprints. The hosted service around it does the I/O.
///
/// Two kinds of sample come out. Where the robot stood still for <see cref="RobotSurveyConfig.DwellSecs"/>
/// (every pose within <see cref="RobotSurveyConfig.StationaryM"/> of their centroid), a complete fingerprint:
/// each node's mean raw rssi over the dwell, with the nodes that reported the beacon recently but not during
/// the dwell listed silent. A long pause yields one every dwell period until <see cref="RobotSurveyConfig.MaxDwellSecs"/>,
/// after which the robot is taken to be parked. Where it drove, one-second bins: the bin is placed by
/// interpolating the poses either side of it, only when the robot was below <see cref="RobotSurveyConfig.MaxSpeed"/>
/// and the pose before that lies on the same line (the robot's pose arrives every few seconds, so a bin in
/// the middle of a turn would be placed where the robot never was). A bin only knows the nodes that
/// reported in it; the surface treats the rest as unknown, not silent.
/// </summary>
public sealed class SurveyIngest(RobotSurveyConfig config, string? floorId, double floorZ)
{
    private const double PoseHistorySecs = 120, ReportHistorySecs = 120, SilentLookbackSecs = 120, BinSettleSecs = 6, StraightM = 0.2;

    public readonly record struct Pose(DateTime At, double X, double Y, double? HeadingDeg);
    private readonly record struct Report(DateTime At, string Node, double Raw);

    private readonly List<Pose> _poses = new();
    private readonly List<Report> _reports = new();
    private DateTime? _stillSince, _lastDwellEmit, _lastBinEnd;
    private bool _parked;

    public Pose? LatestPose => _poses.Count > 0 ? _poses[^1] : null;
    public int DwellSamples { get; private set; }
    public int MovingSamples { get; private set; }
    public bool Parked => _parked;

    /// <summary>Robot frame to floorplan metres: floor = rotate(scale * (x, flip_y ? -y : y)) + offset.</summary>
    public (double X, double Y) Transform(double rx, double ry)
    {
        var t = config.Transform;
        var x = t.Scale * rx;
        var y = t.Scale * (t.FlipY ? -ry : ry);
        var a = t.RotationDeg * Math.PI / 180;
        var (c, s) = (Math.Cos(a), Math.Sin(a));
        var ox = t.Offset is { Length: >= 2 } o ? o[0] : 0;
        var oy = t.Offset is { Length: >= 2 } p ? p[1] : 0;
        return (c * x - s * y + ox, s * x + c * y + oy);
    }

    /// <summary>A pose in the robot's own frame.</summary>
    public void AddPose(DateTime at, double rx, double ry, double? headingDeg)
    {
        var (x, y) = Transform(rx, ry);
        if (_poses.Count > 0 && _poses[^1].At >= at) return;
        _poses.Add(new Pose(at, x, y, headingDeg));
        _poses.RemoveAll(p => (at - p.At).TotalSeconds > PoseHistorySecs);
    }

    /// <summary>What a node heard from the beacon, before its rxAdj.</summary>
    public void AddReport(DateTime at, string node, double raw)
    {
        _reports.Add(new Report(at, node, raw));
        if (_reports.Count % 64 == 0) _reports.RemoveAll(r => (at - r.At).TotalSeconds > ReportHistorySecs);
    }

    /// <summary>Fingerprints that became ready by <paramref name="now"/>.</summary>
    public List<Fingerprint> Drain(DateTime now)
    {
        var output = new List<Fingerprint>();
        _reports.RemoveAll(r => (now - r.At).TotalSeconds > ReportHistorySecs);
        if (_poses.Count == 0) return output;

        var dwell = TimeSpan.FromSeconds(config.DwellSecs);
        var window = _poses.Where(p => now - p.At <= dwell).ToList();
        var still = window.Count >= 2 && IsStill(window);
        if (still)
        {
            _stillSince ??= window[0].At;
            var since = _lastDwellEmit ?? _stillSince.Value;
            if ((now - _stillSince.Value).TotalSeconds > config.MaxDwellSecs) _parked = true;
            if (!_parked && now - since >= dwell)
            {
                var sample = Complete(window, now - dwell, now);
                if (sample != null) { output.Add(sample); DwellSamples++; }
                _lastDwellEmit = now;
            }
        }
        else
        {
            _stillSince = null;
            _lastDwellEmit = null;
            _parked = false;
            if (config.Moving) output.AddRange(MovingBins(now));
        }
        return output;
    }

    private bool IsStill(List<Pose> window)
    {
        var cx = window.Average(p => p.X); var cy = window.Average(p => p.Y);
        return window.All(p => Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy)) <= config.StationaryM);
    }

    private Fingerprint? Complete(List<Pose> window, DateTime from, DateTime to)
    {
        var reports = _reports.Where(r => r.At >= from && r.At <= to).ToList();
        var byNode = reports.GroupBy(r => r.Node, StringComparer.OrdinalIgnoreCase).ToList();
        if (byNode.Count < 2) return null;
        var recent = _reports.Where(r => (to - r.At).TotalSeconds <= SilentLookbackSecs).Select(r => r.Node).Distinct(StringComparer.OrdinalIgnoreCase);
        var silent = recent.Where(n => !byNode.Any(g => string.Equals(g.Key, n, StringComparison.OrdinalIgnoreCase))).ToArray();
        return new Fingerprint
        {
            Id = Guid.NewGuid(),
            Floor = floorId,
            X = window.Average(p => p.X), Y = window.Average(p => p.Y), Z = floorZ + config.Height,
            Heading = window[^1].HeadingDeg,
            Taken = to,
            Source = "robot",
            Rssi = byNode.ToDictionary(g => g.Key, g => g.Average(r => r.Raw), StringComparer.OrdinalIgnoreCase),
            Count = byNode.ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase),
            StdDev = byNode.ToDictionary(g => g.Key, g => StdDev(g.Select(r => r.Raw)), StringComparer.OrdinalIgnoreCase),
            Silent = silent.Length > 0 ? silent : null,
        };
    }

    private IEnumerable<Fingerprint> MovingBins(DateTime now)
    {
        // Bins old enough that the pose after them has arrived.
        var settled = now.AddSeconds(-BinSettleSecs);
        var start = _lastBinEnd ?? _reports.Where(r => r.At <= settled).Select(r => (DateTime?)r.At).Min();
        if (start == null) yield break;
        var binStart = new DateTime(start.Value.Ticks - start.Value.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        for (; binStart.AddSeconds(1) <= settled; binStart = binStart.AddSeconds(1))
        {
            var binEnd = binStart.AddSeconds(1);
            _lastBinEnd = binEnd;
            var reports = _reports.Where(r => r.At >= binStart && r.At < binEnd).ToList();
            if (reports.Count == 0) continue;
            var centre = binStart.AddMilliseconds(500);
            if (Place(centre) is not { } at) continue;
            var byNode = reports.GroupBy(r => r.Node, StringComparer.OrdinalIgnoreCase).ToList();
            MovingSamples++;
            yield return new Fingerprint
            {
                Id = Guid.NewGuid(),
                Floor = floorId,
                X = at.X, Y = at.Y, Z = floorZ + config.Height,
                Heading = at.HeadingDeg,
                Taken = centre,
                Source = "moving",
                Rssi = byNode.ToDictionary(g => g.Key, g => g.Average(r => r.Raw), StringComparer.OrdinalIgnoreCase),
                Count = byNode.ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase),
            };
        }
    }

    /// <summary>
    /// Where the robot was at <paramref name="at"/>, interpolated between the poses either side, or null when
    /// it was still (a dwell covers that), too fast, or not driving straight through them.
    /// </summary>
    internal Pose? Place(DateTime at)
    {
        var before = _poses.LastOrDefault(p => p.At <= at);
        var after = _poses.FirstOrDefault(p => p.At > at);
        if (before.At == default || after.At == default) return null;
        var dt = (after.At - before.At).TotalSeconds;
        if (dt <= 0 || dt > 2 * BinSettleSecs) return null;
        var dist = Math.Sqrt((after.X - before.X) * (after.X - before.X) + (after.Y - before.Y) * (after.Y - before.Y));
        var speed = dist / dt;
        if (speed < 0.05 || speed > config.MaxSpeed) return null;
        // Straight: the pose before 'before' lies on the line from it to 'after'.
        var earlier = _poses.LastOrDefault(p => p.At < before.At);
        if (earlier.At != default && (before.At - earlier.At).TotalSeconds <= 2 * BinSettleSecs &&
            DistanceToSegment(before, earlier, after) > StraightM)
            return null;
        var f = (at - before.At).TotalSeconds / dt;
        return new Pose(at, before.X + f * (after.X - before.X), before.Y + f * (after.Y - before.Y), before.HeadingDeg);
    }

    private static double DistanceToSegment(Pose p, Pose a, Pose b)
    {
        var dx = b.X - a.X; var dy = b.Y - a.Y;
        var len2 = dx * dx + dy * dy;
        var t = len2 > 0 ? Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1) : 0;
        var px = a.X + t * dx; var py = a.Y + t * dy;
        return Math.Sqrt((p.X - px) * (p.X - px) + (p.Y - py) * (p.Y - py));
    }

    private static double StdDev(IEnumerable<double> values)
    {
        var list = values.ToList();
        if (list.Count < 2) return 0;
        var mean = list.Average();
        return Math.Sqrt(list.Sum(v => (v - mean) * (v - mean)) / (list.Count - 1));
    }
}
