using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using ESPresense.Converters;
using MathNet.Numerics.Optimization;
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

    [JsonConverter(typeof(RoomConverter))] public Room? Room => BestScenario?.Room;

    [JsonConverter(typeof(FloorConverter))] public Floor? Floor => BestScenario?.Floor;

    public int Confidence => BestScenario?.Confidence ?? 0;

    [JsonIgnore] public bool Check { get; set; }
    [JsonIgnore] public bool Track { get; set; }

    [JsonIgnore] public Scenario? BestScenario { get; set; }
    [JsonIgnore] public IList<Scenario> Scenarios { get; } = new List<Scenario>();

    [JsonConverter(typeof(Point3DConverter))]
    public Point3D? Location => BestScenario?.Location;

    [JsonIgnore] public Room? ReportedRoom { get; set; }

    [JsonIgnore] public DateTime? LastCalculated { get; set; }

    public IEnumerable<KeyValuePair<string, string>> GetDetails()
    {
        yield return new KeyValuePair<string, string>("Best Scenario", $"{BestScenario?.Name}");

        var scenarios = Scenarios.OrderByDescending(s => s.Confidence).ToArray();
        foreach (var s in scenarios.Where(s => s.Room != null))
            yield return new KeyValuePair<string, string>($"{s.Name} Room", $"{s.Room}");
        foreach (var s in scenarios.Where(a => a.Confidence != null))
            yield return new KeyValuePair<string, string>($"{s.Name} Confidence", $"{s.Confidence}");
        foreach (var s in scenarios.Where(a => a.Fixes != null))
            yield return new KeyValuePair<string, string>($"{s.Name} Fixes", $"{s.Fixes}");
        foreach (var s in scenarios.Where(a => a.Error != null))
            yield return new KeyValuePair<string, string>($"{s.Name} Error", $"{s.Error}");
        foreach (var s in scenarios.Where(a => a.Iterations != null))
            yield return new KeyValuePair<string, string>($"{s.Name} Iterations", $"{s.Iterations}");
        foreach (var s in scenarios.Where(a => a.ReasonForExit != ExitCondition.None))
            yield return new KeyValuePair<string, string>($"{s.Name} ReasonForExit", $"{s.ReasonForExit}");
        var deviceNodes = Nodes.Values.Where(dn => dn.Node != null).OrderBy(dn => dn.Distance).ToList();
        foreach (var dn in deviceNodes)
            yield return new KeyValuePair<string, string>($"{dn.Node?.Name} Rssi/@1m", $"{dn.Rssi}/{dn.RefRssi}");
        foreach (var dn in deviceNodes)
            yield return new KeyValuePair<string, string>($"{dn.Node?.Name} Distance", $"{dn.Distance}");
        foreach (var dn in deviceNodes)
            yield return new KeyValuePair<string, string>($"{dn.Node?.Name} Hits", $"{dn.Hits}");
        foreach (var dn in deviceNodes)
            yield return new KeyValuePair<string, string>($"{dn.Node?.Name} Last Hit", $"{dn.LastHit?.ToLocalTime():s}");

        foreach (var s in scenarios)
        {
            yield return new KeyValuePair<string, string>($"{s.Name} X", $"{s.Location.X:##.000}");
            yield return new KeyValuePair<string, string>($"{s.Name} Y", $"{s.Location.Y:##.000}");
            yield return new KeyValuePair<string, string>($"{s.Name} Z", $"{s.Location.Z:##.000}");
        }
    }
}