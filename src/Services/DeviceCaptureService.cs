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
            if (session.Active && session.Matches(e.DeviceId))
                session.Add(e.NodeId, e.Payload);
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
        if (!_sessions.TryGetValue(deviceId, out var session) || !session.Active) return null;
        session.AddPosition(x, y, z, floor);
        return session.Status();
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
        return new CaptureExport(1, deviceId, deviceName, session.StartedUtc, session.EndedUtc, session.Truncated, nodes, session.PositionSnapshot(), session.Snapshot());
    }

    private class CaptureSession(string deviceId, HashSet<string> matchIds)
    {
        private readonly ConcurrentQueue<CaptureMessage> _entries = new();
        private readonly ConcurrentQueue<CapturePosition> _positions = new();
        private int _count;

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
