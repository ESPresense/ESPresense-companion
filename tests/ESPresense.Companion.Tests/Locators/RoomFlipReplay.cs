using System.Globalization;
using System.Text.Json;
using ESPresense.Models;
using ESPresense.Services;
using ESPresense.Companion.Tests.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ESPresense.Companion.Tests.Locators;

/// <summary>
/// Replays a captured MQTT trace of one static beacon through the real scenario pipeline and
/// records the room the allocator would publish at each step. Backs the #2220 regression lock.
///
/// The seam is <see cref="ScenarioFusion.Update"/>: it runs every scenario's locator, feeds the
/// Kalman filter, smooths the per-scenario probabilities and picks the best scenario - i.e. it is
/// where a room flip actually happens - and it touches no MQTT, history or events. Everything
/// above it (publishing, history) is deliberately out of scope.
///
/// Fixture data and provenance: TestData/room-flip-2220/README.md.
/// </summary>
internal static class RoomFlipReplay
{
    /// <summary>One captured MQTT message. `Distance`/`DistVar` are provenance only.</summary>
    internal sealed record Reading(
        DateTime T, string Node, string Id,
        double Rssi, double RefRssi, double? RxAdj, double? RssiVar,
        double Distance, double? DistVar);

    /// <summary>What the allocator would have reported at one step of the replay.</summary>
    internal sealed record Step(DateTime T, string? Room, string? Scenario, int Stations);

    internal sealed class Truth
    {
        public ReplayParams Replay { get; set; } = new();
        public Dictionary<string, BeaconTruth> Beacons { get; set; } = new();
        public List<EventSpec> Events { get; set; } = new();

        public EventSpec Event(string file) =>
            Events.SingleOrDefault(e => e.File == file)
            ?? throw new InvalidOperationException($"truth.yaml has no event for '{file}'");
    }

    internal sealed class ReplayParams
    {
        public double Absorption { get; set; } = 3.0;
        public double TxOffsetDb { get; set; }
    }

    internal sealed class BeaconTruth
    {
        public string? Label { get; set; }
        public string ExpectedRoom { get; set; } = "";
    }

    internal sealed class EventSpec
    {
        public string File { get; set; } = "";
        public string Beacon { get; set; } = "";
        public bool Canonical { get; set; }
        public DateTime WindowStart { get; set; }
        public DateTime WindowEnd { get; set; }
        public int MinSamplesInWindow { get; set; }
    }

    /// <summary>
    /// A clock the replay drives from the trace's own timestamps. Without it the pipeline runs on
    /// wall time: the Kalman update clamps a step to <c>MaxVelocity * dt</c>, so replaying ~12 min
    /// of trace in ~1 s of wall clock gives dt of a millisecond or two, pins both filters to their
    /// first fix, and makes the outcome depend on machine timing rather than on the data.
    /// </summary>
    internal sealed class TraceClock : TimeProvider
    {
        public DateTime UtcNow { get; set; }
        public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);
    }

    private static string DataDir =>
        Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData", "room-flip-2220");

    internal static Truth LoadTruth() =>
        new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build()
            .Deserialize<Truth>(File.ReadAllText(Path.Combine(DataDir, "truth.yaml")));

    internal static string ScenarioYaml(string locators) =>
        locators.TrimEnd() + "\n\n" + File.ReadAllText(Path.Combine(DataDir, "scenario.yaml"));

    internal static List<Reading> LoadReadings(string file, string beaconId)
    {
        var readings = new List<Reading>();
        foreach (var line in File.ReadLines(Path.Combine(DataDir, file)))
        {
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            var id = r.GetProperty("id").GetString()!;
            if (id != beaconId) continue;
            readings.Add(new Reading(
                T: DateTime.ParseExact(r.GetProperty("t").GetString()!, "yyyy-MM-ddTHH:mm:ss.FFFFFF",
                    CultureInfo.InvariantCulture, DateTimeStyles.None),
                Node: r.GetProperty("node").GetString()!,
                Id: id,
                Rssi: r.GetProperty("rssi").GetDouble(),
                RefRssi: r.GetProperty("rssi@1m").GetDouble(),
                RxAdj: Opt(r, "rxAdj"),
                RssiVar: Opt(r, "rssiVar"),
                Distance: r.GetProperty("distance").GetDouble(),
                DistVar: Opt(r, "var")));
        }

        Assert.That(readings, Is.Not.Empty, $"no readings for {beaconId} in {file}");
        return readings;

        static double? Opt(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    }

    /// <summary>
    /// The distance the replay feeds the locators, from raw rssi rather than the logged
    /// `distance` field: d = 10 ^ ((rssi@1m + txOffset - rssi) / (10 * absorption)).
    /// With txOffset = 0 this reproduces the logged distance exactly. See truth.yaml for why it
    /// is not 0 for this corpus.
    /// </summary>
    internal static double DistanceOf(Reading r, ReplayParams p) =>
        Math.Pow(10, (r.RefRssi + p.TxOffsetDb - r.Rssi) / (10 * p.Absorption));

    /// <summary>
    /// Linear-domain scale between logged and fed distances, so stability thresholds measured on
    /// the logged field (ESPA-570) keep their intended tightness after the tx offset is applied.
    /// </summary>
    internal static double DistanceScale(ReplayParams p) =>
        Math.Pow(10, p.TxOffsetDb / (10 * p.Absorption));

    /// <summary>
    /// Steps the pipeline across the trace at <paramref name="stepSeconds"/> of trace time and
    /// returns what the allocator would have reported at each step. Everything that reads a clock
    /// - reading freshness, scenario currency, both Kalman filters - runs on <see cref="TraceClock"/>,
    /// so the result is a function of the data alone and does not depend on how fast the test runs.
    /// </summary>
    internal static async Task<List<Step>> RunAsync(
        string locators, string file, EventSpec spec, ReplayParams p, double stepSeconds = 1.0)
    {
        var readings = LoadReadings(file, spec.Beacon);
        var clock = new TraceClock { UtcNow = readings[0].T };
        await using var fx = await YamlStateFixture.LoadAsync(ScenarioYaml(locators), clock);

        var timeout = TimeSpan.FromSeconds(fx.State.Config!.Timeout);
        var device = new Device(spec.Beacon, null, timeout, time: clock);
        foreach (var scenario in fx.State.GetScenarios(device)) device.Scenarios.Add(scenario);
        Assert.That(device.Scenarios, Is.Not.Empty, "no scenarios - check the locators block");

        var filtering = fx.State.Config!.Filtering ?? new ConfigFiltering();
        var steps = new List<Step>();
        var next = 0;

        for (var t = readings[0].T; t <= spec.WindowEnd; t = t.AddSeconds(stepSeconds))
        {
            clock.UtcNow = t;

            while (next < readings.Count && readings[next].T <= t)
            {
                var r = readings[next++];
                if (!fx.State.Nodes.TryGetValue(r.Node, out var node)) continue; // not in the config
                clock.UtcNow = r.T; // so each reading's LastHit is its own capture time
                var dn = device.Nodes.GetOrAdd(r.Node, _ => new DeviceToNode(device, node));
                dn.ReadMessage(new DeviceMessage
                {
                    // RefRssi carries the offset so the message stays self-consistent with Distance.
                    Distance = DistanceOf(r, p),
                    DistVar = r.DistVar,
                    Rssi = r.Rssi,
                    RefRssi = r.RefRssi + p.TxOffsetDb,
                    RssiRxAdj = r.RxAdj,
                    RssiVar = r.RssiVar
                });
            }

            clock.UtcNow = t;

            // Staleness needs no help now: LastHit is trace time, so DeviceToNode.Current ages out
            // a station that stopped reporting exactly as it would live.
            if (!device.Nodes.Values.Any(n => n.Current)) continue;

            var fusion = ScenarioFusion.Update(device, filtering, t);
            steps.Add(new Step(t, fusion.Best?.Room?.Name, fusion.Best?.Label,
                device.Nodes.Values.Count(n => n.Current)));
        }

        return steps;
    }

    /// <summary>Per-station distance statistics inside the window, for the stability gate.</summary>
    /// <summary>
    /// Per-station distance statistics inside the window, for the stability gate.
    /// <paramref name="spec"/>'s <see cref="EventSpec.MinSamplesInWindow"/> no longer excludes a
    /// station from the result - it only marks it <c>Dense</c>. Everything with at least two
    /// in-window readings is reported, because everything with in-window readings is fed to
    /// fusion, and a gate that silently skipped the sparse stations would claim more than it
    /// checked.
    /// </summary>
    internal static Dictionary<string, (int N, double Sd, double P5ToP95, bool Dense)> WindowStability(
        IEnumerable<Reading> readings, EventSpec spec, ReplayParams p)
    {
        var stats = new Dictionary<string, (int, double, double, bool)>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in readings.Where(r => r.T >= spec.WindowStart && r.T <= spec.WindowEnd)
                     .GroupBy(r => r.Node, StringComparer.OrdinalIgnoreCase))
        {
            var d = g.Select(r => DistanceOf(r, p)).OrderBy(x => x).ToArray();
            if (d.Length < 2) continue; // nothing to measure from a single reading
            var mean = d.Average();
            var sd = Math.Sqrt(d.Sum(x => (x - mean) * (x - mean)) / d.Length);
            stats[g.Key] = (d.Length, sd, Percentile(d, 95) - Percentile(d, 5),
                d.Length >= spec.MinSamplesInWindow);
        }

        return stats;

        static double Percentile(double[] sorted, double pct)
        {
            var rank = (pct / 100.0) * (sorted.Length - 1);
            var lo = (int)Math.Floor(rank);
            var hi = (int)Math.Ceiling(rank);
            return lo == hi ? sorted[lo] : sorted[lo] + (rank - lo) * (sorted[hi] - sorted[lo]);
        }
    }
}
