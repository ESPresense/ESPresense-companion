using System.Threading.Channels;
using ESPresense.Controllers;
using ESPresense.Models;
using ESPresense.Services;
using Newtonsoft.Json;
using Serilog;

namespace ESPresense.Services.Occupancy;

/// <summary>
/// CMP-2 (ESPA-198): the first consumer of CMP-1's <see cref="OccupancyFusion"/> signal. Wires
/// it into the same Home Assistant MQTT autodiscovery mechanism <see cref="AutoDiscovery"/>
/// already uses for device_tracker entities, publishing one binary_sensor (device_class
/// "occupancy") per configured room so HA users can build HVAC automations against
/// "is this room occupied" the same way they already build automations against ESPresense's
/// device-level presence.
///
/// v1 has no CSI ingestion pipeline yet -- that's FW-1 (ESPA-196), still gated on measured
/// hardware -- so the CSI score fed into <see cref="OccupancyFusion.Fuse"/> here is always null.
/// Per the fusion design, a null CSI score degrades gracefully to the existing BLE-only room
/// presence rather than blocking on it, which matches the augmentation thesis: CSI augments BLE,
/// it is never required for BLE-based occupancy to keep working. Feeding a real
/// <see cref="CsiOccupancyDetector"/> reading in here is follow-up work once FW-1 lands CSI
/// frames on the wire.
/// </summary>
public class RoomOccupancyPublisher(State state, IMqttCoordinator mqtt, GlobalEventDispatcher dispatcher) : BackgroundService
{
    private readonly OccupancyFusion _fusion = new();
    private readonly Dictionary<string, AutoDiscovery> _discoveryByRoomId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _lastPublishedOccupied = new(StringComparer.OrdinalIgnoreCase);

    // Coalesces bursts of device-changed events (a single BLE move can touch several rooms) into
    // one recompute pass instead of one MQTT round-trip per device event.
    private readonly Channel<bool> _recomputeRequested = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        void OnDeviceChanged(object? sender, DeviceEventArgs e) => _recomputeRequested.Writer.TryWrite(true);

        dispatcher.DeviceStateChanged += OnDeviceChanged;
        try
        {
            await PublishAllRoomsAsync(stoppingToken);

            await foreach (var _ in _recomputeRequested.Reader.ReadAllAsync(stoppingToken))
                await PublishAllRoomsAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            dispatcher.DeviceStateChanged -= OnDeviceChanged;
        }
    }

    internal async Task PublishAllRoomsAsync(CancellationToken ct)
    {
        var trackedDevices = state.Devices.Values.Where(d => d.Track).ToList();
        foreach (var room in Rooms())
        {
            if (ct.IsCancellationRequested) return;
            await PublishRoomAsync(room, trackedDevices);
        }
    }

    private IEnumerable<Room> Rooms() => state.Floors.Values.SelectMany(f => f.Rooms.Values).Where(r => r.Id != null);

    /// <summary>
    /// true/false/null per <see cref="OccupancyFusion.Fuse"/>'s contract: null only when the
    /// companion has no tracked devices at all (no BLE reading taken system-wide), false when the
    /// tracked-device pipeline is active but none of those devices are currently in this room.
    /// </summary>
    internal static bool? ResolveBlePresence(string roomId, IReadOnlyCollection<Device> trackedDevices)
    {
        if (trackedDevices.Count == 0) return null;
        return trackedDevices.Any(d => string.Equals(d.Room?.Id, roomId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task PublishRoomAsync(Room room, IReadOnlyCollection<Device> trackedDevices)
    {
        var roomId = room.Id!;
        var blePresent = ResolveBlePresence(roomId, trackedDevices);

        // CSI isn't wired in yet (see class remarks) -- Fuse() falls back to BLE-only with a
        // null csiScore, which is exactly what we want until FW-1 lands real CSI frames.
        var occupancy = _fusion.Fuse(roomId, blePresent, csiScore: null);

        if (!_discoveryByRoomId.TryGetValue(roomId, out var discovery))
        {
            discovery = AutoDiscovery.ForRoomOccupancy(room);
            _discoveryByRoomId[roomId] = discovery;
            await discovery.Send(mqtt);
        }

        if (_lastPublishedOccupied.TryGetValue(roomId, out var lastOccupied) && lastOccupied == occupancy.IsOccupied)
            return;

        _lastPublishedOccupied[roomId] = occupancy.IsOccupied;

        Log.Debug("[occupancy] {RoomId} -> {IsOccupied} ({Confidence}, {Reason})", roomId, occupancy.IsOccupied, occupancy.Confidence, occupancy.Reason);

        await mqtt.TryEnqueueAsync(discovery.Message.StateTopic!, occupancy.IsOccupied ? "ON" : "OFF", true);
        await mqtt.TryEnqueueAsync(discovery.Message.JsonAttributesTopic!,
            JsonConvert.SerializeObject(new { confidence = occupancy.Confidence.ToString(), reason = occupancy.Reason }), true);
    }
}
