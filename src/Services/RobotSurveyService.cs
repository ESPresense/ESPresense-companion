using System.Text.Json.Nodes;
using ESPresense.Events;
using ESPresense.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ESPresense.Services;

/// <summary>
/// Makes every run of a robot that knows where it is into a fingerprint survey. The robot's pose is read
/// from a Home Assistant entity attribute (or pushed to POST /api/survey/pose); the beacon's reports come
/// from MQTT; <see cref="SurveyIngest"/> turns the two into fingerprints, which are appended to the
/// fingerprint file in batches and pruned to <see cref="RobotSurveyConfig.MaxFingerprints"/>. While a pose
/// is fresh, the beacon's own located position is compared to it, which is a live end-to-end accuracy
/// measurement every cleaning run (see <see cref="AccuracyService"/>).
/// </summary>
public class RobotSurveyService(ConfigLoader configLoader, State state, IMqttCoordinator mqtt, HomeAssistantClient hass, AccuracyService accuracy,
    ILogger<RobotSurveyService>? logger = null) : BackgroundService
{
    private readonly ILogger _log = logger ?? NullLogger<RobotSurveyService>.Instance;
    private const int FlushEvery = 20;
    private static readonly TimeSpan FlushAfter = TimeSpan.FromSeconds(30), PoseFresh = TimeSpan.FromSeconds(10);

    private readonly object _lock = new();
    private SurveyIngest? _ingest;
    private RobotSurveyConfig? _active;
    private string? _floorId;
    private readonly List<Fingerprint> _pending = new();
    private DateTime _lastFlush = DateTime.UtcNow;
    private string? _mapSeen;
    private readonly List<string> _warnings = new();

    public DateTime? LastPoseAt { get; private set; }
    public (double X, double Y)? LastPose { get; private set; }
    public int Appended { get; private set; }
    public int Pruned { get; private set; }
    public DateTime? LastFlushAt { get; private set; }

    public object Status()
    {
        lock (_lock)
        {
            var cfg = configLoader.Config?.Survey?.Robot;
            return new
            {
                enabled = cfg?.Enabled ?? false,
                device = cfg?.Device,
                floor = _floorId,
                poseSource = cfg?.Pose?.Entity != null ? $"{cfg.Pose.Entity}.{cfg.Pose.Attribute}" : "POST /api/survey/pose",
                homeAssistant = hass.Available,
                lastPoseAt = LastPoseAt,
                lastPose = LastPose is { } p ? new { x = Math.Round(p.X, 2), y = Math.Round(p.Y, 2) } : null,
                parked = _ingest?.Parked ?? false,
                dwellSamples = _ingest?.DwellSamples ?? 0,
                movingSamples = _ingest?.MovingSamples ?? 0,
                pending = _pending.Count,
                appended = Appended,
                pruned = Pruned,
                lastFlushAt = LastFlushAt,
                warnings = _warnings.ToArray(),
            };
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        mqtt.DeviceMessageReceivedAsync += OnDeviceMessage;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var cfg = configLoader.Config?.Survey?.Robot;
                if (cfg is not { Enabled: true } || string.IsNullOrEmpty(cfg.Device))
                {
                    lock (_lock) { _ingest = null; _active = null; }
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }
                Reconfigure(cfg);
                if (cfg.Pose.Entity != null) await PollPoseAsync(cfg, stoppingToken);
                Tick();
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.5, cfg.Pose.PollSecs)), stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            mqtt.DeviceMessageReceivedAsync -= OnDeviceMessage;
            Flush(force: true);
        }
    }

    private void Reconfigure(RobotSurveyConfig cfg)
    {
        lock (_lock)
        {
            if (ReferenceEquals(cfg, _active) && _ingest != null) return;
            _active = cfg;
            _floorId = cfg.Floor ?? state.Floors.Keys.FirstOrDefault();
            var floorZ = _floorId != null && state.Floors.TryGetValue(_floorId, out var floor) && floor.Bounds is { Length: >= 1 } b ? b[0].Z : 0;
            _ingest = new SurveyIngest(cfg, _floorId, floorZ);
            _warnings.Clear();
            if (_floorId == null) _warnings.Add("No floor configured for the robot survey.");
            if (cfg.Pose.Entity != null && !hass.Available) _warnings.Add("No Home Assistant access: as an add-on the Supervisor provides it, otherwise set HASS_URL and HASS_TOKEN.");
            _log.LogInformation("Robot survey: beacon {Device} on floor {Floor}, pose from {Source}", cfg.Device, _floorId, cfg.Pose.Entity ?? "POST /api/survey/pose");
        }
    }

    private Task OnDeviceMessage(DeviceMessageEventArgs e)
    {
        var cfg = _active;
        if (cfg == null || !string.Equals(e.DeviceId, cfg.Device, StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;
        lock (_lock) _ingest?.AddReport(DateTime.UtcNow, e.NodeId, e.Payload.Rssi + (e.Payload.RssiRxAdj ?? 0));
        return Task.CompletedTask;
    }

    private async Task PollPoseAsync(RobotSurveyConfig cfg, CancellationToken ct)
    {
        try
        {
            var attributes = await hass.GetAttributesAsync(cfg.Pose.Entity!, ct);
            if (attributes == null) { Warn($"Entity {cfg.Pose.Entity} not found in Home Assistant."); return; }
            if (cfg.Pose.MapAttribute != null)
            {
                var map = attributes[cfg.Pose.MapAttribute]?.ToJsonString();
                if (_mapSeen == null) _mapSeen = map;
                else if (map != _mapSeen) { Warn($"The robot's {cfg.Pose.MapAttribute} changed; its poses are in another frame, so surveying is paused."); return; }
            }
            if (attributes[cfg.Pose.Attribute] is not JsonObject pose ||
                !TryNumber(pose["x"], out var x) || !TryNumber(pose["y"], out var y))
            {
                Warn($"Attribute {cfg.Pose.Attribute} of {cfg.Pose.Entity} has no x/y.");
                return;
            }
            double? heading = TryNumber(pose["a"], out var a) ? a : null;
            PushPose(x, y, heading);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Warn($"Reading the pose failed: {ex.Message}");
        }
    }

    private static bool TryNumber(JsonNode? node, out double value)
    {
        value = 0;
        return node is JsonValue v && (v.TryGetValue(out value) || (v.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value)));
    }

    /// <summary>A pose in the robot's frame, from the poller or from POST /api/survey/pose.</summary>
    public bool PushPose(double x, double y, double? headingDeg)
    {
        lock (_lock)
        {
            if (_ingest == null || _active == null) return false;
            var now = DateTime.UtcNow;
            _ingest.AddPose(now, x, y, headingDeg);
            LastPoseAt = now;
            LastPose = _ingest.Transform(x, y);
            // The beacon's own fix against where the robot says it is: the end-to-end ruler.
            if (state.Devices.TryGetValue(_active.Device!, out var device) && device.Location is { } fix &&
                device.LastCalculated is { } at && now - at <= PoseFresh && LastPose is { } truth)
                accuracy.RecordRobot(Math.Sqrt((fix.X - truth.X) * (fix.X - truth.X) + (fix.Y - truth.Y) * (fix.Y - truth.Y)));
            return true;
        }
    }

    private void Tick()
    {
        lock (_lock)
        {
            if (_ingest == null) return;
            var ready = _ingest.Drain(DateTime.UtcNow);
            _pending.AddRange(ready);
        }
        Flush(force: false);
    }

    private void Flush(bool force)
    {
        List<Fingerprint> batch;
        RobotSurveyConfig? cfg;
        lock (_lock)
        {
            cfg = _active;
            if (_pending.Count == 0 || cfg == null) return;
            if (!force && _pending.Count < FlushEvery && DateTime.UtcNow - _lastFlush < FlushAfter) return;
            batch = _pending.ToList();
            _pending.Clear();
            _lastFlush = DateTime.UtcNow;
        }
        try
        {
            var map = state.FingerprintMapFor(configLoader.Config?.Locators?.Fingerprint ?? new FingerprintConfig());
            map.AppendRange(batch);
            lock (_lock) { Appended += batch.Count; LastFlushAt = DateTime.UtcNow; }
            var pruned = map.Prune(cfg.MaxFingerprints);
            if (pruned > 0) lock (_lock) Pruned += pruned;
            _log.LogInformation("Robot survey: appended {Count} fingerprints ({Pruned} pruned)", batch.Count, pruned);
        }
        catch (Exception ex)
        {
            Warn($"Writing fingerprints failed: {ex.Message}");
            lock (_lock) _pending.InsertRange(0, batch);
        }
    }

    private void Warn(string message)
    {
        lock (_lock)
        {
            if (_warnings.Contains(message)) return;
            _warnings.Add(message);
            if (_warnings.Count > 5) _warnings.RemoveAt(0);
        }
        _log.LogWarning("Robot survey: {Message}", message);
    }
}
