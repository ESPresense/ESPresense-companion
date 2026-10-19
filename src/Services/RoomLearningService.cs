using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ESPresense.Events;
using ESPresense.Models;
using MQTTnet;

namespace ESPresense.Services;

/// <summary>
/// Buffers recent radio reports, queues explicit MQTT corrections, and applies them only from the
/// locator's lease-protected processing loop. Publishing a prediction never creates an example.
/// </summary>
public sealed class RoomLearningService : IDisposable
{
    private readonly IMqttCoordinator _mqtt;
    private readonly State _state;
    private readonly DeviceSettingsStore _settings;
    private readonly ILogger<RoomLearningService> _logger;
    private readonly TimeProvider _clock;
    private readonly string _path;
    private RoomFingerprintStore? _store;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _updates = new(1, 1);
    private readonly Dictionary<string, Queue<Report>> _recent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Correction> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Correction> _last = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Payload, DateTime Sent)> _discovery = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Value, DateTime Sent)> _published = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (Room Room, DateTime Until)> _holds = new(StringComparer.OrdinalIgnoreCase);
    private sealed record Report(DateTime At, string Node, double Rssi);
    private sealed record Correction(DateTime At, string Floor, string Room, Dictionary<string, double> Reading);

    public event Action<Device>? CorrectionRequested;

    public void ReloadTraining()
    {
        lock (_gate) _store = null;
    }

    public RoomLearningService(IMqttCoordinator mqtt, State state, DeviceSettingsStore settings, ConfigLoader config,
        ILogger<RoomLearningService> logger, TimeProvider? clock = null)
    {
        _mqtt = mqtt;
        _state = state;
        _settings = settings;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _path = Path.Combine(config.ConfigDirectory, "room-fingerprints.json");
        mqtt.DeviceMessageReceivedAsync += OnReading;
        mqtt.MqttMessageReceivedAsync += OnCommand;
    }

    private bool Enabled => _state.Config?.RoomLearning.Enabled == true;
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;
    private string Profile(string id) => _settings.Get(id)?.OriginalId ?? id;
    public static string StateTopic(string id) => $"espresense/companion/{id}/room";
    public static string DiscoveryTopic(IMqttCoordinator mqtt, string id) =>
        $"{mqtt.DiscoveryTopic}/select/espresense-room-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant()}/config";

    private IEnumerable<Room> Rooms() => (_state.Config?.Floors ?? Array.Empty<ConfigFloor>())
        .SelectMany(f => (_state.Floors.GetValueOrDefault(f.GetId())?.Rooms.Values ?? Enumerable.Empty<Room>())
            .Where(r => f.Rooms?.Any(c => c.GetId() == r.Id) == true));

    private Task OnReading(DeviceMessageEventArgs e)
    {
        if (!Enabled) return Task.CompletedTask;
        var id = _settings.Get(e.DeviceId)?.Id ?? e.DeviceId;
        if (!_state.Devices.TryGetValue(id, out var device)) _state.Devices.TryGetValue(e.DeviceId, out device);
        if (device == null || !device.Track || device.IsAnchored) return Task.CompletedTask;
        var raw = e.Payload.Rssi + (e.Payload.RssiRxAdj ?? 0);
        if (!double.IsFinite(raw)) return Task.CompletedTask;
        lock (_gate)
        {
            if (!_recent.TryGetValue(device.Id, out var reports)) _recent[device.Id] = reports = new();
            reports.Enqueue(new(Now, e.NodeId, raw));
            while (reports.Count > 2000 || (reports.TryPeek(out var old) && Now - old.At > TimeSpan.FromSeconds(10))) reports.Dequeue();
        }
        return Task.CompletedTask;
    }

    private Dictionary<string, double> Reading(string id)
    {
        if (!_recent.TryGetValue(id, out var reports)) return new(StringComparer.OrdinalIgnoreCase);
        return reports.Where(r => Now - r.At <= TimeSpan.FromSeconds(10))
            .GroupBy(r => r.Node, StringComparer.OrdinalIgnoreCase)
            .Where(g => Now - g.Max(r => r.At) <= TimeSpan.FromSeconds(3))
            .ToDictionary(g => g.Key, g => g.Average(r => r.Rssi), StringComparer.OrdinalIgnoreCase);
    }

    private Task OnCommand(MqttApplicationMessageReceivedEventArgs e)
    {
        QueueCorrection(e.ApplicationMessage.Topic, e.ApplicationMessage.ConvertPayloadToString(), e.ApplicationMessage.Retain);
        return Task.CompletedTask;
    }

    internal void QueueCorrection(string topic, string roomName, bool retained)
    {
        if (!Enabled || retained) return;
        var parts = topic.Split('/');
        if (parts is not ["espresense", "companion", var id, "room", "set"] ||
            !_state.Devices.TryGetValue(id, out var device) || !device.Track || device.IsAnchored) return;
        // Room names are expected to be unique. Reject an ambiguous command rather than mislabel it.
        var rooms = Rooms().Where(r => r.Name == roomName).ToArray();
        if (rooms.Length != 1 || rooms[0].Id == null || rooms[0].Floor?.Id == null) return;
        lock (_gate)
        {
            var reading = Reading(id);
            if (reading.Count < 3)
            {
                _logger.LogInformation("Ignoring room correction for {Device}: fewer than three fresh receivers", id);
                return;
            }
            var correction = new Correction(Now, rooms[0].Floor!.Id!, rooms[0].Id!, reading);
            if (_last.TryGetValue(id, out var last) && last.Floor == correction.Floor && last.Room == correction.Room &&
                Now - last.At < TimeSpan.FromSeconds(30)) return;
            _last[id] = correction;
            _pending[id] = correction;
        }
        CorrectionRequested?.Invoke(device);
    }

    /// <summary>Called by the active locator, after its coordinate scenarios have been evaluated.</summary>
    public async Task UpdateAsync(Device device)
    {
        await _updates.WaitAsync();
        try { await UpdateCoreAsync(device); }
        finally { _updates.Release(); }
    }

    private async Task UpdateCoreAsync(Device device)
    {
        if (!Enabled)
        {
            if (_discovery.ContainsKey(device.Id) || _published.ContainsKey(device.Id))
                await RemoveCoreAsync(device, false);
            else
                ClearDevice(device);
            return;
        }
        if (!device.Track || !_state.Devices.TryGetValue(device.Id, out var current) || !ReferenceEquals(current, device)) return;
        var rooms = Rooms().Where(r => !string.IsNullOrWhiteSpace(r.Name)).ToArray();
        var now = Now;
        string? error = null;
        lock (_gate)
        {
            device.LearnedRoom = null;
            if (!device.IsAnchored)
            {
                try
                {
                    _store ??= new RoomFingerprintStore(_path);
                    if (_pending.Remove(device.Id, out var correction) && now - correction.At < TimeSpan.FromSeconds(15))
                    {
                        var room = rooms.FirstOrDefault(r => r.Id == correction.Room && r.Floor?.Id == correction.Floor);
                        if (room != null)
                        {
                            _store.Add(new(Profile(device.Id), correction.Floor, correction.Room, correction.At, correction.Reading));
                            _holds[device.Id] = (room, now.AddSeconds(10));
                        }
                    }
                    var reading = Reading(device.Id);
                    if (reading.Count >= 3)
                    {
                        Room? learned = null;
                        var source = "learned";
                        if (_holds.TryGetValue(device.Id, out var hold) && now < hold.Until && rooms.Contains(hold.Room))
                        {
                            learned = hold.Room;
                            source = "correction";
                        }
                        else
                        {
                            var match = RoomFingerprintStore.Predict(reading, _store.ForDevice(Profile(device.Id))
                                .Where(s => rooms.Any(r => r.Id == s.Room && r.Floor?.Id == s.Floor)));
                            if (match != null) learned = rooms.First(r => r.Id == match.Room && r.Floor?.Id == match.Floor);
                        }
                        if (learned != null) device.LearnedRoom = new(learned, now.AddSeconds(3), source);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                {
                    error = "Room learning unavailable: check room-fingerprints.json and directory permissions.";
                    _logger.LogError(ex, "Could not read or save room corrections");
                }
            }
        }

        var payload = JsonSerializer.Serialize(new
        {
            name = "Room (correct to teach)", unique_id = $"espresense-companion-{device.Id}-room",
            command_topic = StateTopic(device.Id) + "/set", state_topic = StateTopic(device.Id),
            json_attributes_topic = StateTopic(device.Id) + "/attributes",
            availability_topic = "espresense/companion/status", optimistic = false, retain = false,
            options = rooms.Select(r => r.Name).Distinct().OrderBy(n => n).ToArray(),
            device = new { identifiers = new[] { $"espresense-{device.Id}" }, name = device.Name ?? device.Id },
            origin = new { name = "ESPresense Companion" }
        });
        if (!_discovery.TryGetValue(device.Id, out var sent) || sent.Payload != payload || now - sent.Sent > TimeSpan.FromMinutes(1))
            if (await _mqtt.TryEnqueueAsync(DiscoveryTopic(_mqtt, device.Id), payload, true)) _discovery[device.Id] = (payload, now);
        var value = (device.IsAnchored || device.Nodes.Values.Any(n => n.Current)) && rooms.Contains(device.Room!) ? device.Room!.Name! : "None";
        if (!_published.TryGetValue(device.Id, out var previous) || previous.Value != value || now - previous.Sent > TimeSpan.FromSeconds(10))
        {
            if (await _mqtt.TryEnqueueAsync(StateTopic(device.Id), value, true)) _published[device.Id] = (value, now);
            await _mqtt.TryEnqueueAsync(StateTopic(device.Id) + "/attributes", JsonSerializer.Serialize(new
            {
                source = device.LearnedRoom?.Source ?? "position", error, floor = device.Floor?.Name,
                correction_hint = "Select your actual room to teach from the last 10 seconds of readings."
            }), true);
        }
    }

    public async Task RemoveAsync(Device device, bool forgetSamples)
    {
        await _updates.WaitAsync();
        try { await RemoveCoreAsync(device, forgetSamples); }
        finally { _updates.Release(); }
    }

    private void ClearDevice(Device device)
    {
        lock (_gate)
        {
            _recent.Remove(device.Id);
            _pending.Remove(device.Id);
            _last.Remove(device.Id);
            _holds.Remove(device.Id);
            _discovery.Remove(device.Id);
            _published.Remove(device.Id);
            device.LearnedRoom = null;
        }
    }

    private async Task RemoveCoreAsync(Device device, bool forgetSamples)
    {
        var publishCleanup = Enabled || _discovery.ContainsKey(device.Id) || _published.ContainsKey(device.Id);
        ClearDevice(device);
        if (!publishCleanup) return;
        lock (_gate)
        {
            if (forgetSamples)
            {
                try
                {
                    _store ??= new RoomFingerprintStore(_path);
                    _store.Forget(Profile(device.Id));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                {
                    _logger.LogError(ex, "Could not remove room examples for {Device}; continuing MQTT cleanup", device.Id);
                }
            }
        }
        await _mqtt.EnqueueAsync(DiscoveryTopic(_mqtt, device.Id), null, true);
        foreach (var topic in new[] { StateTopic(device.Id), StateTopic(device.Id) + "/attributes", StateTopic(device.Id) + "/set" })
            await _mqtt.EnqueueAsync(topic, null, true);
    }

    public void Dispose()
    {
        _mqtt.DeviceMessageReceivedAsync -= OnReading;
        _mqtt.MqttMessageReceivedAsync -= OnCommand;
    }
}
