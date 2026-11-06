using ESPresense.Extensions;
using ESPresense.Models;

namespace ESPresense.Optimizers;

/// <summary>
/// Rolling history of located devices' readings, windowed for <see cref="DeviceResidualOptimizer"/>.
///
/// The other optimizers only see node-to-node links and anchored devices. How a node hears devices in
/// the room is a different propagation regime (main lobe, not the back of two wall-mounted antennas),
/// and it is only visible in the readings of devices being located. Per device, readings are pooled into
/// fixed windows (window_secs); for each node heard in a window the median rssi is kept with the rxAdj it
/// was received under, so a window can be re-ranged with any candidate rxAdj. Positions are not stored:
/// the optimizer re-solves each window with the calibration it is evaluating.
///
/// Anchored devices are excluded (their position is known; they reach the optimizers as node-to-node
/// style measures already). Windows heard by fewer than min_nodes positioned nodes are dropped: a
/// leave-one-out fit needs enough other nodes to place the device without the one being judged.
/// Memory is bounded by history_hours and by <see cref="MaxWindows"/>.
/// </summary>
public sealed class DeviceResidualStore
{
    /// <summary>
    /// Most windows kept (about 15 MB). When exceeded, the devices holding the most windows are thinned, so a
    /// few chatty devices cannot crowd out the rest.
    /// </summary>
    internal const int MaxWindows = 50_000;

    private readonly object _lock = new();
    private readonly Dictionary<string, Track> _tracks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Queue<DeviceWindow>> _windows = new(StringComparer.OrdinalIgnoreCase);
    private int _count;
    private long _lastSweep = long.MinValue;
    private TimeSpan _window = new ConfigDeviceResidual().Window;
    private int _minNodes = new ConfigDeviceResidual().EffectiveMinNodes;
    private TimeSpan _history = new ConfigDeviceResidual().History;

    private sealed class Track
    {
        /// <summary>Hits per node already taken, so a reading is counted once however often the device is located.</summary>
        public readonly Dictionary<string, int> SeenHits = new(StringComparer.OrdinalIgnoreCase);
        public long Bucket = long.MinValue;
        public readonly Dictionary<string, NodeReadings> Nodes = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<double> Refs = new();
    }

    private sealed class NodeReadings
    {
        /// <summary>Raw rssi (rssi + rxAdj) when rxAdj is known, else the published rssi.</summary>
        public readonly List<double> Values = new();
        public double? LastRxAdj;
    }

    /// <summary>Closed windows currently held.</summary>
    public int Count
    {
        get { lock (_lock) return _count; }
    }

    /// <summary>
    /// Takes the readings <paramref name="device"/> has received since it was last observed. Called by the
    /// locator after each device update. Anchored devices are dropped from the history.
    /// </summary>
    public void Observe(Device device, DateTime now, ConfigDeviceResidual settings)
    {
        lock (_lock)
        {
            Configure(settings);
            if (device.IsAnchored)
            {
                Forget(device.Id);
                return;
            }

            var track = _tracks.GetOrAdd(device.Id, _ => new Track());
            var fresh = new List<(DateTime Hit, string NodeId, double Rssi, double? RxAdj, double RefRssi)>();
            foreach (var dn in device.Nodes.Values)
            {
                var node = dn.Node;
                // A node that moves (stationary: false) has no fixed place to learn an rxAdj for.
                if (node is not { HasLocation: true, Stationary: true } || dn.LastHit is not { } hit) continue;
                if (track.SeenHits.TryGetValue(node.Id, out var seen) && seen == dn.Hits) continue;
                track.SeenHits[node.Id] = dn.Hits;
                fresh.Add((hit, node.Id, dn.Rssi, dn.RssiRxAdj, dn.RefRssi));
            }
            // In time order, so a batch that straddles a window boundary is not half dropped as late.
            foreach (var r in fresh.OrderBy(r => r.Hit))
                AddLocked(device.Id, track, r.Hit, r.NodeId, r.Rssi, r.RxAdj, r.RefRssi);

            // Queries close windows themselves; sweeping here only bounds memory, so once per window is enough.
            var bucket = now.Ticks / _window.Ticks;
            if (bucket > _lastSweep)
            {
                _lastSweep = bucket;
                CloseFinished(now);
            }
        }
    }

    /// <summary>
    /// Adds one reading. <paramref name="rssi"/> is what the node published (raw - rxAdj) and
    /// <paramref name="rxAdj"/> the adjustment it applied. The caller passes positioned, stationary nodes only.
    /// </summary>
    public void Add(string deviceId, DateTime time, string nodeId, double rssi, double? rxAdj, double refRssi, ConfigDeviceResidual settings)
    {
        lock (_lock)
        {
            Configure(settings);
            AddLocked(deviceId, _tracks.GetOrAdd(deviceId, _ => new Track()), time, nodeId, rssi, rxAdj, refRssi);
        }
    }

    private void Configure(ConfigDeviceResidual settings)
    {
        if (settings.Window != _window)
        {
            // Open windows are numbered in the old length; start them afresh.
            foreach (var track in _tracks.Values)
            {
                track.Nodes.Clear();
                track.Refs.Clear();
                track.Bucket = long.MinValue;
            }
            _lastSweep = long.MinValue;
        }
        _window = settings.Window;
        _minNodes = settings.EffectiveMinNodes;
        _history = settings.History;
    }

    private void AddLocked(string deviceId, Track track, DateTime time, string nodeId, double rssi, double? rxAdj, double refRssi)
    {
        if (!double.IsFinite(rssi)) return;
        var bucket = time.Ticks / _window.Ticks;
        if (bucket < track.Bucket) return; // late reading for a window already closed
        if (bucket > track.Bucket)
        {
            CloseLocked(deviceId, track);
            track.Bucket = bucket;
        }

        var readings = track.Nodes.GetOrAdd(nodeId, _ => new NodeReadings());
        readings.Values.Add(rxAdj is { } adj ? rssi + adj : rssi);
        readings.LastRxAdj = rxAdj;
        if (double.IsFinite(refRssi) && refRssi != 0) track.Refs.Add(refRssi);
    }

    /// <summary>Turns a device's open window into a <see cref="DeviceWindow"/> if enough nodes heard it, and resets it.</summary>
    private void CloseLocked(string deviceId, Track track)
    {
        if (track.Bucket == long.MinValue || track.Nodes.Count == 0) return;
        try
        {
            if (track.Nodes.Count < _minNodes || track.Refs.Count == 0) return;
            var readings = track.Nodes
                .Select(n =>
                {
                    var median = Median(n.Value.Values);
                    var rxAdj = n.Value.LastRxAdj;
                    return new DeviceWindowReading(n.Key, rxAdj is { } adj ? median - adj : median, rxAdj);
                })
                .ToArray();
            var start = new DateTime(track.Bucket * _window.Ticks, DateTimeKind.Utc);
            var window = new DeviceWindow
            {
                DeviceId = deviceId,
                Start = start,
                End = start + _window,
                RefRssi = Median(track.Refs),
                Readings = readings,
            };
            _windows.GetOrAdd(deviceId, _ => new Queue<DeviceWindow>()).Enqueue(window);
            _count++;
        }
        finally
        {
            track.Nodes.Clear();
            track.Refs.Clear();
        }
    }

    /// <summary>Closes every open window that ended by <paramref name="now"/>, then drops what has aged out.</summary>
    private void CloseFinished(DateTime now)
    {
        var current = now.Ticks / _window.Ticks;
        foreach (var (id, track) in _tracks)
            if (track.Bucket < current && track.Nodes.Count > 0)
                CloseLocked(id, track);
        Prune(now);
    }

    private void Prune(DateTime now)
    {
        var expiry = now - _history;
        foreach (var (id, queue) in _windows.ToList())
        {
            while (queue.Count > 0 && queue.Peek().End < expiry)
            {
                queue.Dequeue();
                _count--;
            }
            if (queue.Count == 0) _windows.Remove(id);
        }

        // Forget devices that have not reported for a whole history: random-address devices come and go.
        foreach (var (id, track) in _tracks.ToList())
            if (track.Nodes.Count == 0 && !_windows.ContainsKey(id) && track.Bucket != long.MinValue
                && new DateTime(track.Bucket * _window.Ticks, DateTimeKind.Utc) < expiry)
                _tracks.Remove(id);

        if (_count <= MaxWindows) return;

        // Over the cap: trim to 90% of it (so this does not run every sweep). The largest quota q with
        // sum(min(n_d, q)) <= target; devices above it are thinned evenly over their history, newest kept,
        // so the history still spans history_hours rather than only its most recent part.
        var target = MaxWindows * 9 / 10;
        var sizes = _windows.Values.Select(q => q.Count).OrderBy(n => n).ToArray();
        int remaining = target, quota = int.MaxValue;
        for (int i = 0; i < sizes.Length; i++)
        {
            var share = remaining / (sizes.Length - i);
            if (sizes[i] <= share) { remaining -= sizes[i]; continue; }
            quota = Math.Max(1, share);
            break;
        }
        foreach (var (id, queue) in _windows.ToList())
        {
            if (queue.Count <= quota) continue;
            var all = queue.ToArray();
            var step = (double)all.Length / quota;
            var kept = new Queue<DeviceWindow>(quota);
            for (int k = quota - 1; k >= 0; k--) kept.Enqueue(all[all.Length - 1 - (int)(k * step)]);
            _count -= all.Length - kept.Count;
            _windows[id] = kept;
        }
    }

    private void Forget(string deviceId)
    {
        if (_windows.Remove(deviceId, out var queue)) _count -= queue.Count;
        _tracks.Remove(deviceId);
    }

    /// <summary>
    /// Closed windows that ended after <paramref name="after"/> (exclusive) and by <paramref name="until"/>
    /// (inclusive), oldest first. Open windows that have ended by <paramref name="now"/> are closed first.
    /// </summary>
    public List<DeviceWindow> Windows(DateTime? after, DateTime? until, DateTime now)
    {
        lock (_lock)
        {
            CloseFinished(now);
            var result = new List<DeviceWindow>();
            foreach (var queue in _windows.Values)
                foreach (var w in queue)
                    if ((after == null || w.End > after) && (until == null || w.End <= until))
                        result.Add(w);
            result.Sort((a, b) => a.End.CompareTo(b.End));
            return result;
        }
    }

    /// <summary>Closes open windows that ended by <paramref name="now"/>.</summary>
    public void Flush(DateTime now)
    {
        lock (_lock) CloseFinished(now);
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 1) return values[0];
        var sorted = values.ToArray();
        Array.Sort(sorted);
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
