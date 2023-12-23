using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Models;

public class Floor
{
    [JsonIgnore]
    public Config? Config { get; private set; }

    public string? Id { get;private  set; }
    public string? Name { get; private set; }
    public Point3D[]? Bounds { get; private set; }

    public ConcurrentDictionary<string, Room> Rooms { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Update(Config c, ConfigFloor cf)
    {
        Config = c;
        Name = cf.Name;
        Id = cf.GetId();
        Bounds = cf.Bounds?.Select(b => new Point3D(b[0], b[1], b[2])).ToArray();

        foreach (var room in cf.Rooms ?? Enumerable.Empty<ConfigRoom>()) Rooms.GetOrAdd(room.GetId(), a => new Room()).Update(c, this, room);
    }


    public override string ToString()
    {
        return $"{nameof(Id)}: {Id}";
    }

    public bool Contained(double? z)
    {
        if (z == null) return false;
        if (Bounds == null) return false;
        return z >= Bounds[0].Z && z <= Bounds[1].Z;
    }
}