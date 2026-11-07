using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ESPresense.Models;

/// <summary>One surveyed spot: where it is, and the raw rssi (rssi + rxAdj) each node heard there.</summary>
public sealed class Fingerprint
{
    [JsonPropertyName("id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? Id { get; init; }
    [JsonPropertyName("device"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? DeviceId { get; init; }
    [JsonPropertyName("floor")] public string? Floor { get; init; }
    [JsonPropertyName("x"), JsonRequired] public double X { get; init; }
    [JsonPropertyName("y"), JsonRequired] public double Y { get; init; }
    [JsonPropertyName("z")] public double Z { get; init; }
    [JsonPropertyName("rssi"), JsonRequired] public Dictionary<string, double> Rssi { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Where the sample came from: null or "teach" for a complete reading taken standing still (a node absent
    /// from <see cref="Rssi"/> was not heard), "robot" for a survey robot dwell, "moving" for a time bin taken
    /// while the robot drove. For "moving" a node absent from <see cref="Rssi"/> is unknown, not silent, unless
    /// listed in <see cref="Silent"/>: at one report per node every few seconds a one-second bin only hears a few.
    /// </summary>
    [JsonPropertyName("source"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Source { get; init; }
    /// <summary>Nodes known to have heard nothing during the sample; only meaningful with a sparse <see cref="Source"/>.</summary>
    [JsonPropertyName("silent"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string[]? Silent { get; init; }
    /// <summary>Beacon heading in degrees, when the surveyor knew it; the field is kept for a heading-aware match later.</summary>
    [JsonPropertyName("heading"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Heading { get; init; }
    [JsonPropertyName("t"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateTime? Taken { get; init; }
    /// <summary>Reports averaged per node and their spread, when the sample was an average.</summary>
    [JsonPropertyName("n"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Dictionary<string, int>? Count { get; init; }
    [JsonPropertyName("sd"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Dictionary<string, double>? StdDev { get; init; }

    /// <summary>Whether a node absent from <see cref="Rssi"/> means "not heard" (complete samples) or "no data" (sparse bins).</summary>
    [JsonIgnore] public bool AbsentMeansSilent => Source is null or "teach" or "robot";
}

/// <summary>
/// The fingerprint survey, read from a JSON file beside config.yaml:
/// <c>{"fingerprints": [{"floor": "first", "x": 1, "y": 2, "z": 0.3, "rssi": {"node_a": -71.2, ...}}]}</c>.
/// Re-read when the file changes.
/// </summary>
public class FingerprintMap(string path, ILogger<FingerprintMap>? logger = null)
{
    private readonly ILogger _log = logger ?? NullLogger<FingerprintMap>.Instance;
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
                    var parsed = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(Path))?.Fingerprints
                        ?? throw new InvalidDataException("no \"fingerprints\" list");
                    // Check every entry before replacing the loaded survey: a hand-edited file with one bad
                    // entry keeps the last good survey instead of breaking every device's locator.
                    for (var i = 0; i < parsed.Count; i++)
                    {
                        if (parsed[i]?.Rssi == null) throw new InvalidDataException($"fingerprint {i} has no rssi");
                        // System.Text.Json builds the dictionary itself; make node ids case-insensitive.
                        parsed[i].Rssi = new Dictionary<string, double>(parsed[i].Rssi, StringComparer.OrdinalIgnoreCase);
                    }
                    _all = parsed;
                    _log.LogInformation("Loaded {Count} fingerprints from {Path}", _all.Count, Path);
                }
                catch (Exception ex)
                {
                    _log.LogError("Error reading fingerprints from {Path}, keeping the last good survey: {Message}", Path, ex.Message);
                }
                _loadedAt = fi.LastWriteTimeUtc;
                return _all;
            }
        }
    }

    /// <summary>Append without overwriting an unreadable survey; replace the file atomically.</summary>
    public void Append(Fingerprint fingerprint) => AppendRange(new[] { fingerprint });

    /// <summary>Appends many at once: one read and one atomic write for the batch.</summary>
    public void AppendRange(IEnumerable<Fingerprint> fingerprints)
    {
        lock (_lock)
        {
            var data = File.Exists(Path)
                ? JsonNode.Parse(File.ReadAllText(Path)) as JsonObject
                    ?? throw new InvalidDataException("Invalid fingerprint file.")
                : new JsonObject { ["fingerprints"] = new JsonArray() };
            if (data["fingerprints"] is not JsonArray entries) throw new InvalidDataException("Invalid fingerprint list.");
            var any = false;
            foreach (var fingerprint in fingerprints) { entries.Add(JsonSerializer.SerializeToNode(fingerprint)); any = true; }
            if (any) WriteDocument(data);
        }
    }

    /// <summary>Remove only the identified sample belonging to this device, preserving other survey data.</summary>
    public bool Remove(Guid sampleId, string deviceId)
    {
        lock (_lock)
        {
            if (!File.Exists(Path)) return false;
            var data = JsonNode.Parse(File.ReadAllText(Path)) as JsonObject
                ?? throw new InvalidDataException("Invalid fingerprint file.");
            if (data["fingerprints"] is not JsonArray entries) throw new InvalidDataException("Invalid fingerprint list.");
            var matches = entries.OfType<JsonObject>().Where(entry =>
                entry["id"] is JsonValue id && id.TryGetValue<string>(out var value) && Guid.TryParse(value, out var parsed) && parsed == sampleId &&
                entry["device"] is JsonValue owner && owner.TryGetValue<string>(out var name) && string.Equals(name, deviceId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0) return false;
            if (matches.Length > 1) throw new InvalidDataException("Duplicate fingerprint ID.");
            entries.Remove(matches[0]);
            WriteDocument(data);
            return true;
        }
    }

    private void WriteDocument(JsonObject data)
    {
        var temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(data));
            File.Move(temporary, Path, overwrite: true);
            _loadedAt = default;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public IEnumerable<Fingerprint> ForFloor(string? floorId) =>
        All.Where(f => f.Floor == null || string.Equals(f.Floor, floorId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every node id the floor's fingerprints recorded.</summary>
    public HashSet<string> NodesForFloor(string? floorId) =>
        ForFloor(floorId).SelectMany(f => f.Rssi.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<Fingerprint>? _surfaceSource;
    private readonly Dictionary<(string?, double, double, int, double), Locators.FingerprintSurface> _surfaces = new();

    /// <summary>
    /// The continuous surface for one floor, rebuilt when the file reloads or the settings change.
    /// <paramref name="nodeLocation"/> lets the surface regress each placed node's rssi as a residual over a
    /// path-loss prior; <paramref name="version"/> should change when node positions do.
    /// </summary>
    public Locators.FingerprintSurface Surface(string? floorId, double bandwidth, double missingRssi,
        Func<string, MathNet.Spatial.Euclidean.Point3D?>? nodeLocation = null, int version = 0, double sigmaFloor = Locators.FingerprintSurface.DefaultSigmaFloor)
    {
        var all = All;
        lock (_lock)
        {
            if (!ReferenceEquals(all, _surfaceSource)) { _surfaces.Clear(); _surfaceSource = all; }
            var key = (floorId?.ToLowerInvariant(), bandwidth, missingRssi, version, sigmaFloor);
            if (!_surfaces.TryGetValue(key, out var s))
                _surfaces[key] = s = new Locators.FingerprintSurface(ForFloor(floorId).ToList(), bandwidth, missingRssi, nodeLocation, sigmaFloor: sigmaFloor);
            return s;
        }
    }

    /// <summary>Drops robot samples beyond <paramref name="max"/>, oldest first, keeping every taught sample.</summary>
    public int Prune(int max)
    {
        lock (_lock)
        {
            if (!File.Exists(Path)) return 0;
            var data = JsonNode.Parse(File.ReadAllText(Path)) as JsonObject ?? throw new InvalidDataException("Invalid fingerprint file.");
            if (data["fingerprints"] is not JsonArray entries || entries.Count <= max) return 0;
            var robot = entries.OfType<JsonObject>()
                .Where(e => e["source"] is JsonValue v && v.TryGetValue<string>(out var src) && src is "robot" or "moving")
                .OrderBy(e => e["t"] is JsonValue t && t.TryGetValue<DateTime>(out var at) ? at : DateTime.MinValue)
                .Take(entries.Count - max)
                .ToArray();
            foreach (var e in robot) entries.Remove(e);
            if (robot.Length > 0) WriteDocument(data);
            return robot.Length;
        }
    }
}
