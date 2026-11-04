using ESPresense.Models;
using ESPresense.Utils;

namespace ESPresense.Services;

/// <summary>
/// Resolves which <see cref="Room"/> a node physically sits in, by point-in-polygon against the rooms
/// on each floor the node is configured as visible from. This is the same test <see cref="SpatialUtils"/>
/// already uses for a device's located position (<c>Room.Polygon.EnclosesPoint</c>, from MathNet.Spatial),
/// just applied to the node's fixed map location instead of a located point.
/// </summary>
public static class NodeRoomResolver
{
    public static Room? Resolve(Node node)
    {
        if (!node.HasLocation) return null;
        var floors = node.Floors;
        if (floors == null) return null;

        foreach (var floor in floors)
        {
            if (!floor.Contained(node.Location.Z)) continue;
            var room = SpatialUtils.FindRoomContaining(node.Location, floor);
            if (room != null) return room;
        }

        return null;
    }
}
