using System.Text.Json.Serialization;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Models;

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