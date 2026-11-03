using System.Text.Json;

namespace ESPresense.Models;

public sealed record RoomFingerprint(string Device, string Floor, string Room, DateTime Captured,
    Dictionary<string, double> Rssi);

/// <summary>Explicit room corrections only. Never trains on a predicted room or assigns coordinates.</summary>
public sealed class RoomFingerprintStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private List<RoomFingerprint> _samples;

    public RoomFingerprintStore(string path)
    {
        _path = path;
        _samples = File.Exists(path)
            ? JsonSerializer.Deserialize<List<RoomFingerprint>>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid room fingerprints.")
            : new();
        if (_samples.Any(s => s == null || s.Rssi == null || s.Device == null || s.Floor == null || s.Room == null || s.Rssi.Values.Any(v => !double.IsFinite(v))))
            throw new InvalidDataException("Invalid room fingerprint.");
        _samples = _samples.Select(s => s with { Rssi = new(s.Rssi, StringComparer.OrdinalIgnoreCase) }).ToList();
    }

    public IReadOnlyList<RoomFingerprint> ForDevice(string id)
    {
        lock (_gate) return _samples.Where(s => string.Equals(s.Device, id, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public void Add(RoomFingerprint sample)
    {
        lock (_gate)
        {
            // A new correction replaces an almost identical old example, including a mistaken label.
            var others = _samples.Where(s => !string.Equals(s.Device, sample.Device, StringComparison.OrdinalIgnoreCase)).ToList();
            var own = _samples.Where(s => string.Equals(s.Device, sample.Device, StringComparison.OrdinalIgnoreCase))
                .Where(s => Distance(sample.Rssi, s.Rssi) > 0.5).Append(sample).OrderByDescending(s => s.Captured).Take(200);
            Save(others.Concat(own).ToList());
        }
    }

    public void Forget(string id)
    {
        lock (_gate)
        {
            var remaining = _samples.Where(s => !string.Equals(s.Device, id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (remaining.Count != _samples.Count) Save(remaining);
        }
    }

    private void Save(List<RoomFingerprint> samples)
    {
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(samples));
            File.Move(temporary, _path, true);
            _samples = samples;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static RoomFingerprint? Predict(IReadOnlyDictionary<string, double> reading, IEnumerable<RoomFingerprint> samples)
    {
        var best = samples.Select(s => (Sample: s, Error: Distance(reading, s.Rssi)))
            .GroupBy(s => (s.Sample.Floor, s.Sample.Room))
            .Select(g => g.MinBy(s => s.Error))
            .OrderBy(s => s.Error).Take(2).ToArray();
        // Sparse corrections should only override ranging near a known signal pattern.
        // A close tie between rooms is ambiguous: let the existing locator decide.
        if (best.Length == 0 || best[0].Error > 3 || (best.Length > 1 && best[1].Error - best[0].Error < 1)) return null;
        return best[0].Sample;
    }

    public static double Distance(IReadOnlyDictionary<string, double> reading, IReadOnlyDictionary<string, double> sample)
    {
        var shared = reading.Keys.Where(sample.ContainsKey).ToArray();
        if (shared.Length < 3 || shared.Length < Math.Max(reading.Count, sample.Count) * 0.75) return double.PositiveInfinity;
        var deltas = shared.Select(n => reading[n] - sample[n]).ToArray();
        var offset = deltas.Average();
        var variance = deltas.Sum(d => (d - offset) * (d - offset)) / (shared.Length - 1);
        // Presence/absence is information too; a missing receiver must not improve a match.
        return Math.Sqrt(variance + 16 * (reading.Count + sample.Count - 2 * shared.Length));
    }
}
