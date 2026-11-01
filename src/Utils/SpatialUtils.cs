using ESPresense.Extensions;
using ESPresense.Models;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Utils;

/// <summary>
/// Utilities for spatial lookups and geometric operations
/// </summary>
public static class SpatialUtils
{
    /// <summary>
    /// Finds the floor that contains the given 3D location based on floor bounds
    /// </summary>
    public static Floor? FindFloorContaining(Point3D location, IEnumerable<Floor> floors)
    {
        foreach (var floor in floors)
        {
            if (floor.Bounds is not { Length: >= 2 })
                continue;

            var min = floor.Bounds[0];
            var max = floor.Bounds[1];

            if (location.X >= min.X && location.X <= max.X &&
                location.Y >= min.Y && location.Y <= max.Y &&
                location.Z >= min.Z && location.Z <= max.Z)
            {
                return floor;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the floors whose z-range contains z, falling back to the floor nearest in z
    /// </summary>
    public static List<Floor> FindFloorsAt(double z, IEnumerable<Floor> floors)
    {
        var bounded = floors.Where(f => f.Bounds is { Length: >= 2 }).ToList();
        var containing = bounded.Where(f => f.Contained(z)).ToList();
        if (containing.Count > 0) return containing;

        var nearest = bounded.MinBy(f => Math.Max(0, Math.Max(f.Bounds![0].Z - z, z - f.Bounds[1].Z)));
        return nearest == null ? [] : [nearest];
    }

    /// <summary>
    /// Finds the room within a floor that contains the given location
    /// </summary>
    public static Room? FindRoomContaining(Point3D location, Floor? floor)
    {
        if (floor == null)
            return null;

        return floor.Rooms.Values.FirstOrDefault(room =>
            room.Polygon?.EnclosesPoint(location.ToPoint2D()) ?? false);
    }

    /// <summary>
    /// Finds the room for a located device: the room containing the point, otherwise the nearest
    /// room whose boundary is within <paramref name="maxDistance"/> metres.
    ///
    /// A multilateration fix is metres uncertain, but room polygons leave gaps at walls and
    /// doorways and stop at the outside wall, so a fix for someone indoors often lands in no room
    /// at all. The location stays as solved; only the room label falls back, so neither the Kalman
    /// filter nor motion weighting is biased towards room boundaries.
    /// </summary>
    public static Room? FindRoomNearest(Point3D location, IEnumerable<Room> rooms, double maxDistance)
    {
        var point = location.ToPoint2D();
        Room? nearest = null;
        var nearestDistance = maxDistance;

        foreach (var room in rooms)
        {
            var polygon = room.Polygon;
            if (polygon == null) continue;
            if (polygon.EnclosesPoint(point)) return room;

            foreach (var edge in polygon.Edges)
            {
                var distance = edge.ClosestPointTo(point).DistanceTo(point);
                if (!(distance <= nearestDistance)) continue; // written so a NaN fix never gets a room
                nearestDistance = distance;
                nearest = room;
            }
        }

        return nearest;
    }

    /// <summary>
    /// Finds both floor and room containing the given location
    /// </summary>
    public static (Floor? floor, Room? room) FindFloorAndRoom(Point3D location, IEnumerable<Floor> floors)
    {
        var floor = FindFloorContaining(location, floors);
        var room = FindRoomContaining(location, floor);
        return (floor, room);
    }
}
