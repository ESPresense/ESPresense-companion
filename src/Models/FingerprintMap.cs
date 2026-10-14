using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace ESPresense.Models;

/// <summary>One surveyed spot: where it is, and the raw rssi (rssi + rxAdj) each node heard there.</summary>
public sealed class Fingerprint
{
    [JsonPropertyName("floor")] public string? Floor { get; init; }
    [JsonPropertyName("x")] public double X { get; init; }
    [JsonPropertyName("y")] public double Y { get; init; }
    [JsonPropertyName("z")] public double Z { get; init; }
    [JsonPropertyName("rssi")] public Dictionary<string, double> Rssi { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The fingerprint survey, read from a JSON file beside config.yaml:
/// <c>{"fingerprints": [{"floor": "first", "x": 1, "y": 2, "z": 0.3, "rssi": {"node_a": -71.2, ...}}]}</c>.
/// Re-read when the file changes.
/// </summary>
public class FingerprintMap(string path)
{
    private sealed class FileShape
    {
        [JsonPropertyName("fingerprints")] public List<Fingerprint> Fingerprints { get; init; } = new();
    }

    private readonly object _lock = new();
    private DateTime _loadedAt;
    private IReadOnlyList<Fingerprint> _all = Array.Empty<Fingerprint>();

    public string Path { get; } = path;

    public IReadOnlyList<Fingerprint> All
    {
        get
        {
            lock (_lock)
            {
                var fi = new FileInfo(Path);
                if (!fi.Exists) return _all = Array.Empty<Fingerprint>();
                if (fi.LastWriteTimeUtc == _loadedAt) return _all;
                try
                {
                    var parsed = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(Path));
                    _all = parsed?.Fingerprints ?? new List<Fingerprint>();
                    // System.Text.Json builds the dictionary itself; make node ids case-insensitive.
                    foreach (var f in _all)
                        f.Rssi = new Dictionary<string, double>(f.Rssi, StringComparer.OrdinalIgnoreCase);
                    Log.Information("Loaded {Count} fingerprints from {Path}", _all.Count, Path);
                }
                catch (Exception ex)
                {
                    Log.Error("Error reading fingerprints from {Path}: {Message}", Path, ex.Message);
                }
                _loadedAt = fi.LastWriteTimeUtc;
                return _all;
            }
        }
    }

    public IEnumerable<Fingerprint> ForFloor(string? floorId) =>
        All.Where(f => f.Floor == null || string.Equals(f.Floor, floorId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every node id the floor's fingerprints recorded.</summary>
    public HashSet<string> NodesForFloor(string? floorId) =>
        ForFloor(floorId).SelectMany(f => f.Rssi.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<Fingerprint>? _surfaceSource;
    private readonly Dictionary<(string?, double, double), Locators.FingerprintSurface> _surfaces = new();

    /// <summary>The continuous surface for one floor, rebuilt when the file reloads or the settings change.</summary>
    public Locators.FingerprintSurface Surface(string? floorId, double bandwidth, double missingRssi)
    {
        var all = All;
        lock (_lock)
        {
            if (!ReferenceEquals(all, _surfaceSource)) { _surfaces.Clear(); _surfaceSource = all; }
            var key = (floorId?.ToLowerInvariant(), bandwidth, missingRssi);
            if (!_surfaces.TryGetValue(key, out var s))
                _surfaces[key] = s = new Locators.FingerprintSurface(ForFloor(floorId).ToList(), bandwidth, missingRssi);
            return s;
        }
    }
}
