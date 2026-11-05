using ESPresense.Models;
using ESPresense.Utils;

namespace ESPresense.Locators;

/// <summary>
/// Fallback locator that uses the nearest node's location when other locators can't work.
/// This is used when there aren't enough nodes for trilateration.
/// </summary>
internal class NearestNode : ILocate
{
    private readonly Device _device;
    private readonly State _state;

    public NearestNode(Device device, State state)
    {
        _device = device;
        _state = state;
    }

    public bool Locate(Scenario scenario)
    {
        var device = _device;
        var maxDistance = _state.Config?.Locators?.NearestNode?.MaxDistance;
        var limited = maxDistance > 0; // unset, 0 or negative means no limit
        var nearest = device.CurrentReadings()
            .Where(a => !limited || a.Distance <= maxDistance)
            .MinBy(a => a.Distance);
        scenario.Trace = new LocatorTrace(
            nearest == null ? LocatorOutcome.NoFix : LocatorOutcome.Fallback,
            null,
            nearest == null ? [] : [new LocatorTraceNode(nearest, nearest.Node!.Location, nearest.Distance, null)],
            MaxDistance: limited ? maxDistance : null);
        if (nearest == null) return false;

        var node = nearest.Node!;
        var location = node.Location;
        scenario.UpdateLocation(location);

        // Very low confidence - this is a fallback locator
        // Other locators with trilateration will have much higher confidence
        scenario.Confidence = 1;
        scenario.Fixes = 1;

        // A node's configured floors are the ones it helps locate on, which can include floors it
        // only hears through a ceiling, so the floor comes from where the node physically is.
        // A node with no position defaults to the origin, so its configured floors are all we know.
        IReadOnlyList<Floor> floors = node.HasLocation
            ? SpatialUtils.FindFloorsAt(location.Z, _state.Floors.Values)
            : node.Floors ?? [];

        // Try to find the room:
        // 1. First try to find a room containing the node's location
        // 2. If that fails, try to match the node ID to a room name (nodes are often named after their room)
        scenario.Room = floors.Select(f => SpatialUtils.FindRoomContaining(location, f)).FirstOrDefault(r => r != null)
            // Try matching node ID to room name (case-insensitive)
            ?? floors.SelectMany(f => f.Rooms.Values)
                .FirstOrDefault(r => string.Equals(r.Name, node.Id, StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(r.Name, node.Name, StringComparison.OrdinalIgnoreCase));
        scenario.Floor = scenario.Room?.Floor ?? floors.FirstOrDefault();

        return true;
    }
}