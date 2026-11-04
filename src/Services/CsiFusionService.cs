using ESPresense.Controllers;
using ESPresense.Models;
using Microsoft.Extensions.Logging;

namespace ESPresense.Services;

/// <summary>
/// Fuses per-room CSI motion (<see cref="RoomMotionAggregator"/>) with the BLE room each device's
/// locators already picked (<see cref="Device.BestScenario"/>) to:
///  - break a close tie between two room candidates toward whichever one shows motion,
///  - damp a BLE room change into a room with no corroborating motion, so a brief trilateration
///    wobble doesn't flip the reported room,
///  - attribute "who entered" a room to devices whose room likelihood was heading there around the
///    moment motion started, and flag an occupied room with nobody plausible as an unknown occupant.
///
/// <see cref="Tick"/> is the pure, synchronous core (deterministic given a <paramref name="now"/>), kept
/// separate from the MQTT/event publishing side effects in <see cref="RunTickAsync"/> so the decision
/// logic is directly unit-testable.
/// </summary>
public sealed class CsiFusionService(
    State state,
    CsiMotionTracker tracker,
    IMqttCoordinator mqtt,
    GlobalEventDispatcher dispatcher,
    ILogger<CsiFusionService> logger,
    TimeProvider? clock = null) : BackgroundService
{
    // The flap reference case oscillates every 5-30s with probabilities hovering close but rarely
    // exactly tied (the Filtering.SmoothingWeight default of 0.7 means probability moves gradually
    // each tick, not instantly to 50/50). A margin this wide catches that noise band without firing
    // on a scenario pair that has genuinely separated (e.g. 70/30).
    internal const double TieBreakProbabilityMargin = 0.2;

    // Node/device reports both land roughly every 4-6s; two cycles is enough to tell a real move from
    // one noisy sample without holding a real move for an unreasonably long time.
    internal static readonly TimeSpan DampingWindow = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private sealed record PendingChange(string? ToRoomId, DateTime Since);

    private readonly Dictionary<string, string?> _committedRoomId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingChange> _pendingChange = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, bool> _roomMotionPrev = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _motionStartedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _who = new(StringComparer.OrdinalIgnoreCase);

    // Written only from the tick thread (ExecuteAsync/RunTickAsync), but read concurrently from request
    // threads via CurrentOccupancy (StateController.GetRooms). A plain Dictionary enumerated by one
    // thread while another writes it can throw InvalidOperationException or hand back a torn read; the
    // other dictionaries above are tick-thread-only and don't need this.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, RoomOccupancy> _lastOccupancy = new(StringComparer.OrdinalIgnoreCase);

    // Rooms whose disabled-state OFF snapshot has not yet been confirmed published (see RunTickAsync).
    // Kept separate from _lastOccupancy, which is cleared immediately so CurrentOccupancy/GET reflect
    // "disabled" right away -- this is the only copy of what still needs to reach HA if MQTT was down
    // at the moment fusion was disabled.
    private readonly Dictionary<string, RoomOccupancy> _pendingDisabledPublish = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Current occupancy for every CSI-mapped room, as of the last tick.</summary>
    public IReadOnlyDictionary<string, RoomOccupancy> CurrentOccupancy => _lastOccupancy;

    private bool Enabled => state.Config?.CsiFusion.Enabled ?? true;
    private TimeSpan AttributionWindow => TimeSpan.FromSeconds(Math.Max(0, state.Config?.CsiFusion.AttributionWindowSecs ?? 20));

    /// <summary>Runs one fusion pass and returns the rooms whose occupancy changed since the previous tick.</summary>
    internal List<RoomOccupancy> Tick(DateTime now)
    {
        if (!Enabled)
        {
            // A config reload updates this already-running service in place rather than restarting it, so
            // disabling fusion at runtime must actively clear state, not just stop updating it: otherwise
            // Device.FusedRoom stays pinned to its last value and CurrentOccupancy/HA keep reporting the
            // last-known occupancy forever.
            foreach (var device in state.Devices.Values)
                device.FusedRoom = null;

            if (_lastOccupancy.Count > 0)
            {
                // First disabled tick since this room was last occupied: snapshot the OFF values to
                // retry (see RunTickAsync) and clear the public-facing state immediately.
                foreach (var a in _lastOccupancy.Values)
                    _pendingDisabledPublish[a.RoomId] = new RoomOccupancy(a.RoomId, a.RoomName, false, Array.Empty<string>(), false);

                _committedRoomId.Clear();
                _pendingChange.Clear();
                _roomMotionPrev.Clear();
                _motionStartedAt.Clear();
                _who.Clear();
                _lastOccupancy.Clear();
            }

            // Keep returning every pending OFF snapshot on every disabled tick -- not just once -- so a
            // publish that silently failed (RoomOccupancyDiscovery.Publish's TryEnqueueAsync returning
            // false, e.g. MQTT down) gets retried instead of being lost for good the moment _lastOccupancy
            // was cleared above. RunTickAsync removes an entry once it confirms that room's publish
            // actually succeeded.
            return _pendingDisabledPublish.Values.ToList();
        }
        _pendingDisabledPublish.Clear(); // re-enabled: any stale OFF retry is no longer relevant

        var roomMotion = RoomMotionAggregator.Compute(state, tracker, now, logger);

        foreach (var device in state.Devices.Values)
            DecideDeviceRoom(device, roomMotion, now);

        var changed = new List<RoomOccupancy>();
        foreach (var (roomId, motion) in roomMotion)
        {
            var occupancy = UpdateRoomOccupancy(roomId, motion, now);
            if (!_lastOccupancy.TryGetValue(roomId, out var previous) || !IsSameOccupancy(previous, occupancy))
            {
                _lastOccupancy[roomId] = occupancy;
                changed.Add(occupancy);
            }
        }

        return changed;
    }

    internal async Task RunTickAsync()
    {
        List<RoomOccupancy> changed;
        try
        {
            changed = Tick(_clock.GetUtcNow().UtcDateTime);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error running CSI fusion tick");
            return;
        }

        foreach (var occupancy in changed)
        {
            try
            {
                dispatcher.OnRoomOccupancyChanged(occupancy);
                if (await RoomOccupancyDiscovery.Publish(mqtt, occupancy))
                    _pendingDisabledPublish.Remove(occupancy.RoomId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error publishing room occupancy for {RoomId}", occupancy.RoomId);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunTickAsync();
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (TaskCanceledException) { /* shutting down */ }
        }
    }

    // RoomOccupancy's Who is a freshly-materialized array each tick (see UpdateRoomOccupancy), so the
    // record's default reference-based equality on that property would call every occupied tick "changed".
    private static bool IsSameOccupancy(RoomOccupancy a, RoomOccupancy b) =>
        a.RoomId == b.RoomId && a.RoomName == b.RoomName && a.Occupied == b.Occupied && a.Unknown == b.Unknown
        && a.Who.SequenceEqual(b.Who);

    private Room? FindRoom(string? roomId) =>
        roomId == null ? null : state.Floors.Values.SelectMany(f => f.Rooms.Values).FirstOrDefault(r => r.Id == roomId);

    /// <summary>
    /// Tie-break and damping. A device that is anchored, not tracked, or whose candidate/target room has
    /// no CSI coverage at all falls straight through to <c>BestScenario.Room</c> (FusedRoom left null),
    /// which is what makes the no-CSI-data case byte-for-byte identical to today's BLE-only resolution.
    /// </summary>
    internal void DecideDeviceRoom(Device device, IReadOnlyDictionary<string, RoomMotion> roomMotion, DateTime now)
    {
        if (device.IsAnchored || !device.Track)
        {
            device.FusedRoom = null;
            return;
        }

        var bleRoomId = device.BestScenario?.Room?.Id;
        string? fusedId;

        var candidates = device.Scenarios
            .Where(s => s.Current && s.Room?.Id != null)
            .GroupBy(s => s.Room!.Id!)
            .Select(g => (RoomId: g.Key, Probability: g.Max(s => s.Probability)))
            .OrderByDescending(c => c.Probability)
            .ToList();

        if (candidates.Count >= 2)
        {
            var (topId, topP) = candidates[0];
            var (secondId, secondP) = candidates[1];
            if (topP - secondP < TieBreakProbabilityMargin
                && roomMotion.TryGetValue(topId, out var topMotion)
                && roomMotion.TryGetValue(secondId, out var secondMotion)
                && topMotion.Motion != secondMotion.Motion)
            {
                fusedId = topMotion.Motion ? topId : secondId;
                _committedRoomId[device.Id] = fusedId;
                _pendingChange.Remove(device.Id);
                device.FusedRoom = fusedId == bleRoomId ? null : FindRoom(fusedId);
                return;
            }
        }

        // No prior commit at all (first time this device has ever been decided) means there is nothing
        // to damp away from yet, so the very first room is trusted immediately -- only a *change* away
        // from an already-committed room gets damped.
        var hasCommitted = _committedRoomId.TryGetValue(device.Id, out var committed);
        if (!hasCommitted || bleRoomId == committed)
        {
            fusedId = bleRoomId;
            _pendingChange.Remove(device.Id);
        }
        else if (bleRoomId == null || !roomMotion.TryGetValue(bleRoomId, out var targetMotion) || targetMotion.Motion)
        {
            // No target room, no CSI coverage for it, or motion already confirms the move: nothing to damp.
            fusedId = bleRoomId;
            _pendingChange.Remove(device.Id);
        }
        else
        {
            if (!_pendingChange.TryGetValue(device.Id, out var pending) || pending.ToRoomId != bleRoomId)
                _pendingChange[device.Id] = pending = new PendingChange(bleRoomId, now);

            fusedId = now - pending.Since >= DampingWindow ? bleRoomId : committed; // hold until the window elapses
            if (fusedId == bleRoomId) _pendingChange.Remove(device.Id);
        }

        _committedRoomId[device.Id] = fusedId;
        device.FusedRoom = fusedId == bleRoomId ? null : FindRoom(fusedId);
    }

    /// <summary>
    /// Updates one room's occupancy/attribution state for this tick and returns its current snapshot.
    /// Attribution uses the device's current (post-fusion) room as the "heading there" signal, re-checked
    /// on every tick for <see cref="AttributionWindow"/> after the motion-started edge -- simpler than
    /// tracking each device's probability trajectory, while still only crediting devices seen near the edge.
    /// </summary>
    private RoomOccupancy UpdateRoomOccupancy(string roomId, RoomMotion motion, DateTime now)
    {
        var wasMotion = _roomMotionPrev.GetValueOrDefault(roomId);

        if (motion.Motion && !wasMotion)
        {
            _motionStartedAt[roomId] = now;
            _who[roomId] = DevicesHeadingToward(roomId).ToList();
        }
        else if (!motion.Motion && wasMotion)
        {
            _who.Remove(roomId);
        }
        else if (motion.Motion && _motionStartedAt.TryGetValue(roomId, out var startedAt) && now - startedAt <= AttributionWindow)
        {
            var who = _who.TryGetValue(roomId, out var existing) ? existing : _who[roomId] = new List<string>();
            foreach (var deviceId in DevicesHeadingToward(roomId))
                if (!who.Contains(deviceId)) who.Add(deviceId);
        }

        _roomMotionPrev[roomId] = motion.Motion;

        var who2 = motion.Motion && _who.TryGetValue(roomId, out var w) ? (IReadOnlyList<string>)w.ToArray() : Array.Empty<string>();
        var unknown = motion.Motion && who2.Count == 0;
        var room = FindRoom(roomId);
        return new RoomOccupancy(roomId, room?.Name, motion.Motion, who2, unknown);
    }

    private IEnumerable<string> DevicesHeadingToward(string roomId) =>
        state.Devices.Values.Where(d => d.Track && !d.IsAnchored && d.Room?.Id == roomId).Select(d => d.Id);
}
