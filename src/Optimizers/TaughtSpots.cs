using System.Text.Json;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Optimizers;

/// <summary>
/// Taught locations as spots for the spot score and judge (#1868), read from the fingerprint survey beside config.yaml
/// (<c>{"fingerprints": [{"floor", "x", "y", "z", "rssi": {node: raw}}]}</c>, written by #1753's "I'm here"). Raw RSSI
/// is <c>rssi + rxAdj</c> as taught, so it stays valid when rxAdj changes later.
///
/// Samples taken at the same place (same floor, position rounded to <see cref="GridM"/>) are one spot, each node's
/// RSSI averaged: counting repeat samples as separate spots would let one place outvote the rest of the house, which
/// is the pseudo-replication #1868 found in the snapshot split. A missing, unreadable or malformed file is no spots.
///
/// A receiver only counts for spots on a floor it serves. Heard through a floor or a stairwell, a spot says how much
/// that floor attenuates, not how the receiver is calibrated; fitted or judged on it, rxAdj would absorb the floor
/// loss and be right for one floor and wrong for the other.
/// </summary>
internal sealed class TaughtSpots(string path, ILogger? logger = null)
{
    internal const double GridM = 0.25;

    private readonly object _lock = new();
    private DateTime _loadedAt;
    private List<(string Floor, Point3D At, Dictionary<string, double> Rssi)> _samples = new();
    private bool _warned;

    public string Path { get; } = path;

    /// <summary>
    /// The taught spots, with the receivers that have a known position and serve the spot's floor.
    /// <paramref name="receiver"/> gives a receiver's position and floor ids, or null for one without a position;
    /// a receiver with no floors listed counts on every floor.
    /// </summary>
    public IReadOnlyList<KnownPositionSpot> Read(Func<string, (Point3D At, IReadOnlyCollection<string> Floors)?> receiver)
    {
        List<(string Floor, Point3D At, Dictionary<string, double> Rssi)> samples;
        lock (_lock) samples = Load();

        return samples
            .GroupBy(s => (s.Floor, X: Math.Round(s.At.X / GridM), Y: Math.Round(s.At.Y / GridM)))
            .Select(g =>
            {
                var at = new Point3D(g.Average(s => s.At.X), g.Average(s => s.At.Y), g.Average(s => s.At.Z));
                var readings = g.SelectMany(s => s.Rssi)
                    .GroupBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(n => (Id: n.Key, Raw: n.Average(kv => kv.Value), Rx: receiver(n.Key)))
                    .Where(n => n.Rx is { } rx && (rx.Floors.Count == 0 || g.Key.Floor.Length == 0 || rx.Floors.Contains(g.Key.Floor)))
                    .Select(n => new SpotReading(n.Id, n.Rx!.Value.At, n.Raw))
                    .OrderBy(r => r.RxId, StringComparer.Ordinal)
                    .ToList();
                return new KnownPositionSpot($"taught:{g.Key.Floor}:{at.X:0.00},{at.Y:0.00}", at, readings);
            })
            .Where(s => s.Readings.Count >= SpotScore.MinReceivers)
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .ToList();
    }

    private List<(string, Point3D, Dictionary<string, double>)> Load()
    {
        var fi = new FileInfo(Path);
        if (!fi.Exists) { _loadedAt = default; return _samples = new(); }
        if (fi.LastWriteTimeUtc == _loadedAt) return _samples;
        _loadedAt = fi.LastWriteTimeUtc;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path));
            var list = new List<(string, Point3D, Dictionary<string, double>)>();
            if (doc.RootElement.TryGetProperty("fingerprints", out var fps) && fps.ValueKind == JsonValueKind.Array)
                foreach (var fp in fps.EnumerateArray())
                {
                    if (!fp.TryGetProperty("x", out var x) || !fp.TryGetProperty("y", out var y) || !x.TryGetDouble(out var xd) || !y.TryGetDouble(out var yd)) continue;
                    var zd = fp.TryGetProperty("z", out var z) && z.TryGetDouble(out var zv) ? zv : 0;
                    var floor = fp.TryGetProperty("floor", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() ?? "" : "";
                    var rssi = new Dictionary<string, double>(StringComparer.Ordinal);
                    if (fp.TryGetProperty("rssi", out var r) && r.ValueKind == JsonValueKind.Object)
                        foreach (var n in r.EnumerateObject())
                            if (n.Value.TryGetDouble(out var v) && double.IsFinite(v)) rssi[n.Name] = v;
                    if (rssi.Count > 0 && double.IsFinite(xd) && double.IsFinite(yd)) list.Add((floor, new Point3D(xd, yd, zd), rssi));
                }
            _warned = false;
            return _samples = list;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            if (!_warned) logger?.LogWarning("Taught spots unavailable: could not read {Path}: {Message}", Path, ex.Message);
            _warned = true;
            return _samples = new();
        }
    }
}
