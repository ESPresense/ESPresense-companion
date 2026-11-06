namespace ESPresense.Models;

public class DeviceToNode(Device device, Node node)
{
    /// <summary>Raw rssi reports kept per node for windowed matching; at one report per ~4 s this spans several minutes.</summary>
    private const int HistoryLength = 64;

    private readonly (DateTime At, double Raw)[] _history = new (DateTime, double)[HistoryLength];
    private int _historyCount, _historyNext;

    public Device Device { get; } = device;
    public Node Node { get; } = node;

    public double Distance { get; set; }
    public double? DistVar { get; set; }

    public double Rssi { get; set; }
    /// <summary>The node's rxAdj when it sent this reading; the firmware publishes rssi = raw - rxAdj.</summary>
    public double? RssiRxAdj { get; set; }
    public double? RssiVar { get; set; }
    public double RefRssi { get; set; }

    public DateTime? LastHit { get; set; }
    public int Hits { get; set; }

    public double LastDistance { get; set; }

    public bool Current => DateTime.UtcNow - LastHit < Device!.Timeout;

    // ReadMessage and Snapshot hold this so a copy never mixes fields from two messages.
    private readonly object _gate = new();

    /// <summary>
    /// A detached copy of this reading. MQTT updates the live object from another thread, and a solver
    /// that re-reads it every iteration chases a moving target: it can stop converging and fits a blend
    /// of old and new readings. A newer reading re-queues the device, so solving on a copy loses nothing.
    /// </summary>
    public DeviceToNode Snapshot()
    {
        lock (_gate)
            return new(Device, Node)
            {
                Distance = Distance,
                DistVar = DistVar,
                Rssi = Rssi,
                RssiRxAdj = RssiRxAdj,
                RssiVar = RssiVar,
                RefRssi = RefRssi,
                LastHit = LastHit,
                Hits = Hits,
                LastDistance = LastDistance,
            };
    }

    /// <summary>What the node's radio heard, before its rxAdj: the quantity fingerprints are recorded in.</summary>
    public double RawRssi => Rssi + (RssiRxAdj ?? 0);

    public bool ReadMessage(DeviceMessage payload)
    {
        bool moved;
        lock (_gate)
        {
            Rssi = payload.Rssi;
            RssiRxAdj = payload.RssiRxAdj;
            RssiVar = payload.RssiVar;
            RefRssi = payload.RefRssi;
            moved = Math.Abs(LastDistance - payload.Distance) > 0.25;
            if (moved) LastDistance = payload.Distance;
            Distance = payload.Distance;
            DistVar = payload.DistVar;
            LastHit = DateTime.UtcNow;
            Hits++;
            lock (_history)
            {
                _history[_historyNext] = (LastHit.Value, RawRssi);
                _historyNext = (_historyNext + 1) % HistoryLength;
                if (_historyCount < HistoryLength) _historyCount++;
            }
        }
        NewName(payload.Name);
        Device?.ReadingArrived(); // last, so a snapshot taken after reading the version can't miss these fields
        return moved;
    }

    /// <summary>
    /// Mean raw rssi of the reports received in the last <paramref name="window"/>, with how many there were.
    /// A single report has a few dB of fading noise on it; a device that stays put for 15-30 s averages most of
    /// that away, which is what the fingerprint survey it is matched against did too.
    /// </summary>
    public (double Mean, int Count) RawRssiOver(TimeSpan window)
    {
        var since = DateTime.UtcNow - window;
        double sum = 0; var n = 0;
        lock (_history)
        {
            for (var i = 0; i < _historyCount; i++)
            {
                var (at, raw) = _history[i];
                if (at < since) continue;
                sum += raw; n++;
            }
        }
        return n == 0 ? (RawRssi, 0) : (sum / n, n);
    }

    private void NewName(string? name)
    {
        if (Device == null) return;
        if (string.IsNullOrEmpty(name)) return;
        if (Device.Name == name) return;
        Device.Name = name;
        Device.Check = true;
    }
}
