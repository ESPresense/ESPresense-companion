using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using ESPresense.Converters;
using MathNet.Spatial.Euclidean;
using SQLite;
using ESPresense.Utils;

namespace ESPresense.Models;

public class Node
{
    public Node(string? id)
    {
        Id = id;
    }

    [JsonIgnore]
    public Config? Config { get; private set; }

    [PrimaryKey]
    public string? Id { get; private set; }
    public string? Name { get; private set; }

    public double? X { get; private set; }
    public double? Y { get; private set; }
    public double? Z { get; private set; }
    public bool HasLocation => X.HasValue && Y.HasValue && Z.HasValue;
    public bool Stationary { get; private set; }

    [Ignore][JsonConverter(typeof(Point3DConverter))]
    public Point3D Location
    {
        get => new(X ?? 0, Y ?? 0, Z ?? 0);
        private set
        {
            X = value.X;
            Y = value.Y;
            Z = value.Z;
        }
    }


    public void Update(Config c, ConfigNode cn, IEnumerable<Floor> floors)
    {
        Config = c;
        Name = cn.Name;
        Floors = floors.ToArray();
        double[]? point = cn.Point?.EnsureLength(3);
        Location = new Point3D(point?[0] ?? 0, point?[1] ?? 0, point?[2] ?? 0);
        Stationary = cn.Stationary;
    }

    public Floor[]? Floors { get; private set; }

    public ConcurrentDictionary<string, RxNode> RxNodes { get; } = new(comparer: StringComparer.OrdinalIgnoreCase);

    public override string ToString()
    {
        return $"{nameof(Id)}: {Id}";
    }
}

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

public class Room
{
    [JsonIgnore]
    public Config? Config { get; private set; }

    public string? Id { get;private  set; }
    public string? Name { get; private set; }

    [JsonIgnore]
    public Polygon2D? Polygon { get; private set; }

    public Floor? Floor { get; set; }

    public void Update(Config config, Floor floor, ConfigRoom room)
    {
        Config = config;
        Floor = floor;
        Name = room.Name;
        Id = room.GetId();
        Polygon = new Polygon2D(room.Points?.Select(a => new Point2D(a[0], a[1])) ?? Enumerable.Empty<Point2D>());
    }

    public override string ToString()
    {
        return $"{nameof(Id)}: {Id}";
    }
}