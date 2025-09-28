using MathNet.Spatial.Euclidean;

namespace ESPresense.Models;

public class DeviceAnchor
{
    public DeviceAnchor(Point3D location, Floor? floor, Room? room)
    {
        Location = location;
        Floor = floor;
        Room = room;
    }

    public Point3D Location { get; }
    public Floor? Floor { get; }
    public Room? Room { get; }
}
