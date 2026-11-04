using System;
using System.Collections.Generic;
using System.Linq;

namespace ESPresense.Simulation.FusionValidation;

/// <summary>A room's occupied/unoccupied span per a <c>binary_sensor.*_occupancy</c> entity. <c>End</c> is null while ongoing.</summary>
public sealed record RoomOccupancyInterval(string Room, DateTimeOffset Start, DateTimeOffset? End)
{
    public bool Contains(DateTimeOffset t) => t >= Start && (End is null || t < End);
}

/// <summary>A tagged device's presence in one room per <c>device_tracker.*</c> history. <c>End</c> is null while ongoing.</summary>
public sealed record DeviceRoomInterval(string DeviceId, string Room, DateTimeOffset Start, DateTimeOffset? End)
{
    public bool Contains(DateTimeOffset t) => t >= Start && (End is null || t < End);
}

public sealed class GroundTruth
{
    public List<RoomOccupancyInterval> RoomOccupancy { get; } = new();
    public List<DeviceRoomInterval> DeviceRooms { get; } = new();

    public bool IsRoomOccupied(string room, DateTimeOffset t) =>
        RoomOccupancy.Any(i => i.Room == room && i.Contains(t));

    public string? DeviceRoomAt(string deviceId, DateTimeOffset t) =>
        DeviceRooms.FirstOrDefault(i => i.DeviceId == deviceId && i.Contains(t))?.Room;
}

/// <summary>
/// Turns raw HA state-change history into the two ground-truth series the validation
/// metrics score against. Entities that are known-unreliable (e.g. Family's occupancy
/// sensor, stuck "on" since 2026-11-03 09:03 per ESPA-463) are excluded by the caller
/// rather than hardcoded here, so the exclusion list stays visible at the call site.
/// </summary>
public static class GroundTruthBuilder
{
    private const string NotHomeState = "not_home";
    private const string UnavailableState = "unavailable";
    private const string UnknownState = "unknown";

    public static GroundTruth Build(
        Dictionary<string, List<HaStateChange>> history,
        IReadOnlySet<string> excludedEntityIds,
        Func<string, string> roomFromOccupancyEntity,
        Func<string, string?> roomFromTrackerState)
    {
        var gt = new GroundTruth();

        foreach (var (entityId, changes) in history)
        {
            if (excludedEntityIds.Contains(entityId)) continue;

            if (entityId.StartsWith("binary_sensor.", StringComparison.Ordinal) && entityId.EndsWith("_occupancy", StringComparison.Ordinal))
            {
                var room = roomFromOccupancyEntity(entityId);
                AppendOccupancyIntervals(gt, room, changes);
            }
            else if (entityId.StartsWith("device_tracker.", StringComparison.Ordinal))
            {
                AppendTrackerIntervals(gt, entityId, changes, roomFromTrackerState);
            }
        }

        return gt;
    }

    private static void AppendOccupancyIntervals(GroundTruth gt, string room, List<HaStateChange> changes)
    {
        DateTimeOffset? openStart = null;
        foreach (var c in changes.OrderBy(c => c.LastChanged))
        {
            var on = c.State.Equals("on", StringComparison.OrdinalIgnoreCase);
            if (on && openStart is null)
            {
                openStart = c.LastChanged;
            }
            else if (!on && openStart is not null)
            {
                gt.RoomOccupancy.Add(new RoomOccupancyInterval(room, openStart.Value, c.LastChanged));
                openStart = null;
            }
        }
        if (openStart is not null)
            gt.RoomOccupancy.Add(new RoomOccupancyInterval(room, openStart.Value, null));
    }

    private static void AppendTrackerIntervals(
        GroundTruth gt,
        string entityId,
        List<HaStateChange> changes,
        Func<string, string?> roomFromTrackerState)
    {
        var deviceId = entityId.Substring("device_tracker.".Length);
        string? currentRoom = null;
        DateTimeOffset? currentStart = null;

        foreach (var c in changes.OrderBy(c => c.LastChanged))
        {
            var room = IsAbsentState(c.State) ? null : roomFromTrackerState(c.State);
            if (room == currentRoom) continue;

            if (currentRoom is not null && currentStart is not null)
                gt.DeviceRooms.Add(new DeviceRoomInterval(deviceId, currentRoom, currentStart.Value, c.LastChanged));

            currentRoom = room;
            currentStart = room is null ? null : c.LastChanged;
        }

        if (currentRoom is not null && currentStart is not null)
            gt.DeviceRooms.Add(new DeviceRoomInterval(deviceId, currentRoom, currentStart.Value, null));
    }

    private static bool IsAbsentState(string state) =>
        state.Equals(NotHomeState, StringComparison.OrdinalIgnoreCase)
        || state.Equals(UnavailableState, StringComparison.OrdinalIgnoreCase)
        || state.Equals(UnknownState, StringComparison.OrdinalIgnoreCase);
}
