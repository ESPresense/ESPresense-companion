using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using ESPresense.Converters;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Models;

public class Device
{
    public override string ToString()
    {
        return $"{nameof(Id)}: {Id}";
    }

    public string? Id { get; init; }
    public string? Name { get; set; }

    [JsonIgnore] public Point3D ReportedLocation { get; set; }

    [JsonConverter(typeof(NodeDistanceConverter))]
    public ConcurrentDictionary<string, DeviceNode> Nodes { get; } = new(comparer: StringComparer.OrdinalIgnoreCase);

    [JsonConverter(typeof(RoomConverter))] public Room? Room => BestScenario.Room;

    public bool Check { get; set; }
    public bool Track { get; set; }

    [JsonIgnore] public Scenario BestScenario => Scenarios.OrderByDescending(a => a.Confidence).First();
    [JsonIgnore] public IList<Scenario> Scenarios { get; } = new List<Scenario>();

    [JsonConverter(typeof(Point3DConverter))]
    public Point3D Location => BestScenario.Location;
}