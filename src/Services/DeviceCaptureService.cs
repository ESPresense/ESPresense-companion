using System.Collections.Concurrent;
using ESPresense.Events;
using ESPresense.Models;

namespace ESPresense.Services;

/// <summary>
/// Records raw device MQTT messages for a device so they can be exported and replayed
/// offline (accuracy analysis). One capture session per device id; messages are kept
/// in memory until exported or discarded.
/// Captures every report the firmware sends as-is — node reporting settings are never
/// modified, so the capture faithfully reflects real-world message timing and rate.
/// </summary>
public class DeviceCaptureService
{
    private const int MaxEntries = 250_000;

    private readonly State _state;
    private readonly ConcurrentDictionary<string, CaptureSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public DeviceCaptureService(IMqttCoordinator mqtt, State state)
    {
        _state = state;
        mqtt.DeviceMessageReceivedAsync += OnDeviceMessage;
    }

    private Task OnDeviceMessage(DeviceMessageEventArgs e)
    {
        foreach (var session in _sessions.Values)
            if (session.Matches(e.DeviceId))
                session.TryAdd(e.NodeId, e.Payload);
        return Task.CompletedTask;
    }

    public CaptureStatus Start(string deviceId, params string?[] alternateIds)
    {
        var matchIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { deviceId };
        foreach (var id in alternateIds)
            if (!string.IsNullOrEmpty(id))
                matchIds.Add(id);
        var session = _sessions.AddOrUpdate(deviceId,
            _ => new CaptureSession(deviceId, matchIds),
            (_, existing) => existing.Active ? existing : new CaptureSession(deviceId, matchIds));
        return session.Status();
    }

    public CaptureStatus? Stop(string deviceId)
    {
        if (!_sessions.TryGetValue(deviceId, out var session)) return null;
        session.Finish();
        return session.Status();
    }

    public CaptureStatus? AddPosition(string deviceId, double x, double y, double z, string? floor)
    {
        if (!_sessions.TryGetValue(deviceId, out var session)) return null;
        return session.TryAddPosition(x, y, z, floor) ? session.Status() : null;
    }

    public CaptureStatus? GetStatus(string deviceId)
    {
        return _sessions.TryGetValue(deviceId, out var session) ? session.Status() : null;
    }

    public bool Discard(string deviceId)
    {
        return _sessions.TryRemove(deviceId, out _);
    }

    public CaptureExport? Export(string deviceId, string? deviceName)
    {
        if (!_sessions.TryGetValue(deviceId, out var session)) return null;
        var nodes = _state.Nodes.Values
            .Where(n => n.HasLocation)
            .Select(n => new CaptureNode(n.Id, n.Name, new[] { n.X ?? 0, n.Y ?? 0, n.Z ?? 0 }, n.Floors?.Select(f => f.Id ?? "").ToArray() ?? Array.Empty<string>()))
            .ToArray();
        var snap = session.Snapshot();
        return new CaptureExport(1, deviceId, deviceName, snap.Status.started, snap.Status.ended, snap.Status.truncated, nodes, snap.Positions, snap.Messages);
    }

    /// <summary>
    /// One capture. All state is guarded by <c>_gate</c> (held only for in-memory work, never for I/O or
    /// serialization), so "is it still active?" and "append" are one step: nothing is appended once
    /// <see cref="Finish"/> returns, stop is idempotent, and status/export come from one snapshot.
    /// </summary>
    private class CaptureSession(string deviceId, HashSet<string> matchIds)
    {
        private readonly object _gate = new();
        private readonly List<CaptureMessage> _entries = new();
        private readonly List<CapturePosition> _positions = new();
        private bool _active = true;
        private DateTime? _endedUtc;
        private bool _truncated;

        public DateTime StartedUtc { get; } = DateTime.UtcNow;

        public bool Active { get { lock (_gate) return _active; } }

        public bool Matches(string id) => matchIds.Contains(id);

        public bool TryAdd(string nodeId, DeviceMessage payload)
        {
            lock (_gate)
            {
                if (!_active) return false;
                if (_entries.Count >= MaxEntries)
                {
                    _truncated = true;
                    return false;
                }
                _entries.Add(new CaptureMessage(DateTime.UtcNow, nodeId, payload.Distance, payload.DistVar, payload.Rssi, payload.RssiRxAdj, payload.RssiVar, payload.RefRssi));
                return true;
            }
        }

        public bool TryAddPosition(double x, double y, double z, string? floor)
        {
            lock (_gate)
            {
                if (!_active) return false;
                _positions.Add(new CapturePosition(DateTime.UtcNow, x, y, z, floor));
                return true;
            }
        }

        public void Finish()
        {
            lock (_gate)
            {
                if (!_active) return;
                _active = false;
                _endedUtc = DateTime.UtcNow;
            }
        }

        public (CaptureStatus Status, CaptureMessage[] Messages, CapturePosition[] Positions) Snapshot()
        {
            lock (_gate)
                return (BuildStatus(), _entries.ToArray(), _positions.ToArray());
        }

        public CaptureStatus Status()
        {
            lock (_gate) return BuildStatus();
        }

        private CaptureStatus BuildStatus() => new(deviceId, _active, _entries.Count, _positions.Count, StartedUtc, _endedUtc, _truncated);
    }
}

public readonly record struct CaptureStatus(string deviceId, bool active, int count, int positions, DateTime started, DateTime? ended, bool truncated);

public readonly record struct CapturePosition(DateTime t, double x, double y, double z, string? floor);

public readonly record struct CaptureMessage(DateTime t, string node, double distance, double? distVar, double rssi, double? rxAdj, double? rssiVar, double refRssi);

public readonly record struct CaptureNode(string id, string? name, double[] point, string[] floors);

public readonly record struct CaptureExport(int version, string deviceId, string? deviceName, DateTime started, DateTime? ended, bool truncated, CaptureNode[] nodes, CapturePosition[] positions, CaptureMessage[] messages);
