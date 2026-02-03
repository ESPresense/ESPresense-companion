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
        var nodes = device.Nodes.Values.Where(a => a.Current).OrderBy(a => a.Distance).ToArray();
        var nearest = nodes.FirstOrDefault();
        if (nearest == null) return false;

        var node = nearest.Node!;
        var location = node.Location;
        scenario.UpdateLocation(location);

        // Very low confidence - this is a fallback locator
        // Other locators with trilateration will have much higher confidence
        scenario.Confidence = 1;
        scenario.Fixes = 1;

        // Find the floor containing the node's location; fall back to first configured floor
        scenario.Floor = node.Floors == null
            ? null
            : SpatialUtils.FindFloorContaining(location, node.Floors) ?? node.Floors.FirstOrDefault();

        // Try to find the room:
        // 1. First try to find a room containing the node's location
        // 2. If that fails, try to match the node ID to a room name (nodes are often named after their room)
        scenario.Room = SpatialUtils.FindRoomContaining(location, scenario.Floor);
        if (scenario.Room == null && scenario.Floor != null)
        {
            // Try matching node ID to room name (case-insensitive)
            scenario.Room = scenario.Floor.Rooms.Values
                .FirstOrDefault(r => string.Equals(r.Name, node.Id, StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(r.Name, node.Name, StringComparison.OrdinalIgnoreCase));
        }

        return true;
    }
}