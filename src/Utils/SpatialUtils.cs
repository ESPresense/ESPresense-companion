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
    /// Finds both floor and room containing the given location
    /// </summary>
    public static (Floor? floor, Room? room) FindFloorAndRoom(Point3D location, IEnumerable<Floor> floors)
    {
        var floor = FindFloorContaining(location, floors);
        var room = FindRoomContaining(location, floor);
        return (floor, room);
    }
}
