using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using ESPresense.Locators;
using ESPresense.Models;
using MathNet.Spatial.Euclidean;
using ESPresense.Utils;

namespace ESPresense.Services;

/// <summary>
/// The ruler. Every change to nodes, survey or settings should show up as metres, so three held-out
/// measurements of the fingerprint locator run continuously and are published to Home Assistant:
/// <list type="bullet">
/// <item><b>survey</b>: surveyed spots held out of the map and located from the rest (the robot-survey
/// test's leave-one-spot-out, on the live map), re-scored when the map changes.</item>
/// <item><b>robot</b>: during a robot survey run, the beacon's own fix against where the robot says it is:
/// the whole pipeline, end to end, every cleaning run.</item>
/// <item><b>anchors</b>: anchored devices located as if they were unknown and compared to their anchors.</item>
/// </list>
/// Published as <c>espresense/companion/accuracy</c> and discovered as sensors.
/// </summary>
public class AccuracyService(ConfigLoader configLoader, State state, IMqttCoordinator mqtt, ILogger<AccuracyService>? logger = null) : BackgroundService
{
    private readonly ILogger _log = logger ?? NullLogger<AccuracyService>.Instance;
    public const string Topic = "espresense/companion/accuracy";
    private const double SameSpot = 0.5;
    private const int MaxTraining = 3000;

    /// <summary>The last <c>capacity</c> errors (m) with when they were taken.</summary>
    public sealed class Window(int capacity)
    {
        private readonly Queue<(DateTime At, double Error)> _items = new();
        public void Add(double error)
        {
            lock (_items)
            {
                _items.Enqueue((DateTime.UtcNow, error));
                while (_items.Count > capacity) _items.Dequeue();
            }
        }
        public Summary? Summarize()
        {
            lock (_items)
            {
                if (_items.Count == 0) return null;
                var sorted = _items.Select(i => i.Error).OrderBy(e => e).ToList();
                return new Summary(Pct(sorted, 0.5), Pct(sorted, 0.9), sorted.Count, _items.Max(i => i.At));
            }
        }
    }

    public sealed record Summary(double Median, double P90, int Count, DateTime At);
    public sealed record HoldoutResult(double Median, double P90, int Spots, int Visits, int Fingerprints, DateTime At);

    private readonly Window _robot = new(2000);
    private readonly Window _anchors = new(500);
    private HoldoutResult? _holdout;
    private DateTime _holdoutAt;
    private DateTime _holdoutMapStamp;
    private bool _discovered;
    private Task? _holdoutTask;

    public void RecordRobot(double error) => _robot.Add(error);

    public object Snapshot() => new
    {
        survey = _holdout,
        robot = _robot.Summarize(),
        anchors = _anchors.Summarize(),
    };

    /// <summary>Starts a survey holdout now unless one is running.</summary>
    public bool RequestHoldout()
    {
        if (_holdoutTask is { IsCompleted: false }) return false;
        _holdoutTask = Task.Run(() => RunHoldout(force: true));
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await configLoader.ConfigAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var config = configLoader.Config;
                var accuracy = config?.Accuracy ?? new ConfigAccuracy();
                if (accuracy.Enabled && config?.Locators?.Fingerprint is { Enabled: true })
                {
                    try
                    {
                        CheckAnchors(config);
                        if (_holdoutTask is null or { IsCompleted: true }) _holdoutTask = Task.Run(() => RunHoldout(force: false), stoppingToken);
                        await PublishAsync();
                    }
                    catch (Exception ex) { _log.LogWarning("Accuracy: {Message}", ex.Message); }
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, accuracy.IntervalSecs)), stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void CheckAnchors(Config config)
    {
        var fc = config.Locators.Fingerprint;
        var map = state.FingerprintMapFor(fc);
        if (map.All.Count == 0) return;
        foreach (var device in state.Devices.Values.Where(d => d.IsAnchored && d.Anchor?.Floor != null).ToList())
        {
            var anchor = device.Anchor!;
            var locator = new FingerprintLocator(device, anchor.Floor!, map, fc, fc.Prior ? state.NodeLocation : null, state.LocatorsVersion);
            var scenario = new Scenario(config, locator, anchor.Floor!.Name);
            locator.Locate(scenario);
            if ((scenario.Confidence ?? 0) <= 0) continue;
            var fix = scenario.Fit;
            _anchors.Add(Math.Sqrt((fix.X - anchor.Location.X) * (fix.X - anchor.Location.X) + (fix.Y - anchor.Location.Y) * (fix.Y - anchor.Location.Y)));
        }
    }

    private void RunHoldout(bool force)
    {
        var config = configLoader.Config;
        if (config?.Locators?.Fingerprint is not { Enabled: true } fc) return;
        var accuracy = config.Accuracy ?? new ConfigAccuracy();
        var map = state.FingerprintMapFor(fc);
        var stamp = File.Exists(map.Path) ? File.GetLastWriteTimeUtc(map.Path) : default;
        if (!force && (stamp == _holdoutMapStamp || DateTime.UtcNow - _holdoutAt < TimeSpan.FromMinutes(Math.Max(1, accuracy.HoldoutIntervalMins)))) return;
        var all = map.All;
        if (all.Count < 4) return;
        try
        {
            var result = Holdout(all, fc, fc.Prior ? state.NodeLocation : null, accuracy.HoldoutSpots);
            _holdout = result;
            _holdoutAt = DateTime.UtcNow;
            _holdoutMapStamp = stamp;
            if (result != null) _log.LogInformation("Accuracy: survey holdout median {Median:0.00} m, p90 {P90:0.00} m over {Spots} spots", result.Median, result.P90, result.Spots);
        }
        catch (Exception ex) { _log.LogWarning("Accuracy: survey holdout failed: {Message}", ex.Message); }
    }

    /// <summary>
    /// Holds surveyed spots (complete fingerprints within <see cref="SameSpot"/> of each other) out of the map
    /// in turn and locates each of their readings with the plain locator's match from the rest. Up to
    /// <paramref name="spots"/> spots, chosen reproducibly; the training set is thinned to <see cref="MaxTraining"/>.
    /// Each spot scores the median of its visits, like the robot-survey test.
    /// </summary>
    public static HoldoutResult? Holdout(IReadOnlyList<Fingerprint> all, FingerprintConfig fc, Func<string, Point3D?>? nodeLocation, int spots, int seed = 1)
    {
        var complete = all.Where(f => f.AbsentMeansSilent).ToList();
        var clusters = new List<List<Fingerprint>>();
        foreach (var f in complete)
        {
            var c = clusters.FirstOrDefault(c => Math.Sqrt(Math.Pow(c[0].X - f.X, 2) + Math.Pow(c[0].Y - f.Y, 2)) <= SameSpot && string.Equals(c[0].Floor, f.Floor, StringComparison.OrdinalIgnoreCase));
            if (c == null) clusters.Add(new List<Fingerprint> { f }); else c.Add(f);
        }
        if (clusters.Count < 4) return null;
        var rng = new Random(seed);
        var chosen = clusters.OrderBy(_ => rng.Next()).Take(Math.Max(1, spots)).ToList();
        var errors = new List<double>();
        var visits = 0;
        foreach (var spot in chosen)
        {
            var floor = spot[0].Floor;
            var train = all.Where(f => string.Equals(f.Floor, floor, StringComparison.OrdinalIgnoreCase) &&
                                       Math.Sqrt(Math.Pow(f.X - spot[0].X, 2) + Math.Pow(f.Y - spot[0].Y, 2)) > SameSpot).ToList();
            if (train.Count > MaxTraining) train = train.OrderBy(_ => rng.Next()).Take(MaxTraining).ToList();
            if (train.Count < 3) continue;
            var surface = new FingerprintSurface(train, fc.Bandwidth, fc.MissingRssi ?? -100, nodeLocation, sigmaFloor: fc.SigmaFloor);
            var spotErrors = new List<double>();
            foreach (var visit in spot)
            {
                var match = surface.Match(visit.Rssi, Math.Max(2, fc.MinCommon), fc.Top);
                if (match == null) continue;
                visits++;
                spotErrors.Add(Math.Sqrt(Math.Pow(match.Location.X - visit.X, 2) + Math.Pow(match.Location.Y - visit.Y, 2)));
            }
            if (spotErrors.Count == 0) continue;
            spotErrors.Sort();
            errors.Add(spotErrors[spotErrors.Count / 2]);
        }
        if (errors.Count == 0) return null;
        errors.Sort();
        return new HoldoutResult(Pct(errors, 0.5), Pct(errors, 0.9), errors.Count, visits, all.Count, DateTime.UtcNow);
    }

    private static double Pct(List<double> sorted, double q) => sorted[(int)Math.Round(q * (sorted.Count - 1))];

    private async Task PublishAsync()
    {
        var snapshot = Snapshot();
        if (!await mqtt.TryEnqueueAsync(Topic, JsonSerializer.Serialize(snapshot, JsonDefaults.Mqtt), retain: true)) return;
        if (_discovered) return;
        foreach (var (key, name) in new[] { ("survey", "Survey holdout"), ("robot", "Robot run"), ("anchors", "Anchors") })
            foreach (var stat in new[] { "median", "p90" })
            {
                var record = new AutoDiscovery.DiscoveryRecord
                {
                    Name = $"Fingerprint accuracy {name} {stat}",
                    UniqueId = $"espresense-companion-accuracy-{key}-{stat}",
                    StateTopic = Topic,
                    ValueTemplate = $"{{{{ value_json.{key}.{stat} | round(2) if value_json.{key} else none }}}}",
                    UnitOfMeasurement = "m",
                    StateClass = "measurement",
                    Icon = "mdi:target",
                    EntityCategory = "diagnostic",
                    EntityStatusTopic = "espresense/companion/status",
                    Device = new AutoDiscovery.DeviceRecord { Name = "ESPresense Companion", Manufacturer = "ESPresense", Model = "Companion", Identifiers = new[] { "espresense-companion" } },
                    Origin = new AutoDiscovery.OriginRecord { Name = "ESPresense Companion" },
                };
                await mqtt.TryEnqueueAsync($"{mqtt.DiscoveryTopic}/sensor/espresense-companion-accuracy-{key}-{stat}/config",
                    JsonSerializer.Serialize(record, JsonDefaults.Mqtt), retain: true);
            }
        _discovered = true;
    }
}
