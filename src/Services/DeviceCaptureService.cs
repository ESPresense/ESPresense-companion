using System.Collections.Concurrent;
using ESPresense.Events;
using ESPresense.Models;

namespace ESPresense.Services;

/// <summary>
/// Records raw device MQTT messages for a device so they can be exported and replayed
/// offline (accuracy analysis). One capture session per device id; messages are kept
/// in memory until exported or discarded.
/// While any capture is active, nodes are switched to high-rate reporting
/// (skip_ms lowered so they report every reading instead of throttling); prior
/// values are restored when the last capture stops.
/// </summary>
public class DeviceCaptureService
{
    private const int MaxEntries = 250_000;
    private const int CaptureSkipMs = 500;
    private static readonly TimeSpan MaxCaptureDuration = TimeSpan.FromMinutes(15);

    private readonly State _state;
    private readonly IMqttCoordinator _mqtt;
    private readonly NodeSettingsStore _nodeSettings;
    private readonly ConcurrentDictionary<string, CaptureSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _boostLock = new();
    private int _boostCount;
    private Dictionary<string, int?> _priorSkipMs = new();

    public DeviceCaptureService(IMqttCoordinator mqtt, State state, NodeSettingsStore nodeSettings)
    {
        _state = state;
        _mqtt = mqtt;
        _nodeSettings = nodeSettings;
        mqtt.DeviceMessageReceivedAsync += OnDeviceMessage;
    }

    private Task OnDeviceMessage(DeviceMessageEventArgs e)
    {
        foreach (var session in _sessions.Values)
            if (session.Active && session.Matches(e.DeviceId))
                session.Add(e.NodeId, e.Payload);
        return Task.CompletedTask;
    }

    public async Task<CaptureStatus> Start(string deviceId, params string?[] alternateIds)
    {
        var matchIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { deviceId };
        foreach (var id in alternateIds)
            if (!string.IsNullOrEmpty(id))
                matchIds.Add(id);
        var session = _sessions.AddOrUpdate(deviceId,
            _ => new CaptureSession(deviceId, matchIds),
            (_, existing) => existing.Active ? existing : new CaptureSession(deviceId, matchIds));
        if (session.TryMarkBoosted())
        {
            await EngageBoostAsync();
            _ = AutoStopAsync(deviceId, session);
        }
        return session.Status();
    }

    public async Task<CaptureStatus?> Stop(string deviceId)
    {
        if (!_sessions.TryGetValue(deviceId, out var session)) return null;
        if (session.TryFinish())
            await ReleaseBoostAsync();
        return session.Status();
    }

    public CaptureStatus? AddPosition(string deviceId, double x, double y, double z, string? floor)
    {
        if (!_sessions.TryGetValue(deviceId, out var session) || !session.Active) return null;
        session.AddPosition(x, y, z, floor);
        return session.Status();
    }

    public CaptureStatus? GetStatus(string deviceId)
    {
        return _sessions.TryGetValue(deviceId, out var session) ? session.Status() : null;
    }

    public async Task<bool> Discard(string deviceId)
    {
        if (!_sessions.TryRemove(deviceId, out var session)) return false;
        if (session.TryFinish())
            await ReleaseBoostAsync();
        return true;
    }

    /// <summary>
    /// Lowers skip_ms on all nodes so they report every reading during a capture.
    /// Reference-counted across concurrent captures; priors restored on release.
    /// </summary>
    private async Task EngageBoostAsync()
    {
        Dictionary<string, int?>? priors = null;
        lock (_boostLock)
        {
            if (++_boostCount == 1)
            {
                priors = new Dictionary<string, int?>();
                foreach (var id in _state.Nodes.Keys)
                    priors[id] = _nodeSettings.Get(id).Filtering?.SkipMs;
                _priorSkipMs = priors;
            }
        }
        if (priors == null) return;
        foreach (var (id, prior) in priors)
            await _mqtt.UpdateSetting(id, "skip_ms", (int?)CaptureSkipMs, false, prior);
    }

    private async Task ReleaseBoostAsync()
    {
        Dictionary<string, int?>? priors = null;
        lock (_boostLock)
        {
            if (--_boostCount == 0)
            {
                priors = _priorSkipMs;
                _priorSkipMs = new Dictionary<string, int?>();
            }
        }
        if (priors == null) return;
        foreach (var (id, prior) in priors)
            await _mqtt.UpdateSetting(id, "skip_ms", prior, false, CaptureSkipMs);
    }

    private async Task AutoStopAsync(string deviceId, CaptureSession session)
    {
        try
        {
            await Task.Delay(MaxCaptureDuration);
            if (session.Active && _sessions.TryGetValue(deviceId, out var current) && ReferenceEquals(current, session))
                await Stop(deviceId);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Capture auto-stop failed for {DeviceId}", deviceId);
        }
    }

    public CaptureExport? Export(string deviceId, string? deviceName)
    {
        if (!_sessions.TryGetValue(deviceId, out var session)) return null;
        var nodes = _state.Nodes.Values
            .Where(n => n.HasLocation)
            .Select(n => new CaptureNode(n.Id, n.Name, new[] { n.X ?? 0, n.Y ?? 0, n.Z ?? 0 }, n.Floors?.Select(f => f.Id ?? "").ToArray() ?? Array.Empty<string>()))
            .ToArray();
        return new CaptureExport(1, deviceId, deviceName, session.StartedUtc, session.EndedUtc, session.Truncated, nodes, session.PositionSnapshot(), session.Snapshot());
    }

    private class CaptureSession(string deviceId, HashSet<string> matchIds)
    {
        private readonly ConcurrentQueue<CaptureMessage> _entries = new();
        private readonly ConcurrentQueue<CapturePosition> _positions = new();
        private int _count;
        private int _boosted;
        private int _finished;

        public bool TryMarkBoosted() => Interlocked.Exchange(ref _boosted, 1) == 0;

        public bool TryFinish()
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0) return false;
            Finish();
            return true;
        }

        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        public DateTime? EndedUtc { get; private set; }
        public bool Active { get; private set; } = true;
        public bool Truncated { get; private set; }

        public bool Matches(string id) => matchIds.Contains(id);

        public void Add(string nodeId, DeviceMessage payload)
        {
            if (Interlocked.Increment(ref _count) > MaxEntries)
            {
                Truncated = true;
                Interlocked.Decrement(ref _count);
                return;
            }
            _entries.Enqueue(new CaptureMessage(DateTime.UtcNow, nodeId, payload.Distance, payload.DistVar, payload.Rssi, payload.RssiRxAdj, payload.RssiVar, payload.RefRssi));
        }

        public void AddPosition(double x, double y, double z, string? floor)
        {
            _positions.Enqueue(new CapturePosition(DateTime.UtcNow, x, y, z, floor));
        }

        public void Finish()
        {
            if (!Active) return;
            Active = false;
            EndedUtc = DateTime.UtcNow;
        }

        public CaptureMessage[] Snapshot() => _entries.ToArray();

        public CapturePosition[] PositionSnapshot() => _positions.ToArray();

        public CaptureStatus Status() => new(deviceId, Active, _entries.Count, _positions.Count, StartedUtc, EndedUtc, Truncated);
    }
}

public readonly record struct CaptureStatus(string deviceId, bool active, int count, int positions, DateTime started, DateTime? ended, bool truncated);

public readonly record struct CapturePosition(DateTime t, double x, double y, double z, string? floor);

public readonly record struct CaptureMessage(DateTime t, string node, double distance, double? distVar, double rssi, double? rxAdj, double? rssiVar, double refRssi);

public readonly record struct CaptureNode(string id, string? name, double[] point, string[] floors);

public readonly record struct CaptureExport(int version, string deviceId, string? deviceName, DateTime started, DateTime? ended, bool truncated, CaptureNode[] nodes, CapturePosition[] positions, CaptureMessage[] messages);
