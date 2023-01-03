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

    public IEnumerable<KeyValuePair<string, string>> GetDetails()
    {
        yield return new KeyValuePair<string, string>("Best Scenario", $"{BestScenario.Name}");

        var scenarios = Scenarios.OrderByDescending(s=>s.Confidence).ToArray();
        foreach (var s in scenarios.Where(s=>s.Room!=null))
            yield return new KeyValuePair<string, string>($"{s.Name} Room", $"{s.Room}");

        foreach (var s in scenarios)
            yield return new KeyValuePair<string, string>($"{s.Name} Confidence", $"{s.Confidence}");

        var deviceNodes = Nodes.Values.Where(dn => dn.Node != null).OrderBy(dn => dn.Distance).ToList();
        foreach (var dn in deviceNodes)
            yield return new KeyValuePair<string, string>($"{dn.Node?.Name} Rssi", $"{dn.Rssi}");
        foreach (var dn in deviceNodes)
            yield return new KeyValuePair<string, string>($"{dn.Node?.Name} Distance", $"{dn.Distance}");
        foreach (var dn in deviceNodes)
            yield return new KeyValuePair<string, string>($"{dn.Node?.Name} Hits", $"{dn.Hits}");
        foreach (var dn in deviceNodes)
            yield return new KeyValuePair<string, string>($"{dn.Node?.Name} Last Hit", $"{dn.LastHit?.ToLocalTime():s}");

        foreach (var s in scenarios)
        {
            yield return new KeyValuePair<string, string>($"{s.Name} X", $"{s.Location.X}");
            yield return new KeyValuePair<string, string>($"{s.Name} Y", $"{s.Location.Y}");
            yield return new KeyValuePair<string, string>($"{s.Name} Z", $"{s.Location.Z}");
        }
    }
}