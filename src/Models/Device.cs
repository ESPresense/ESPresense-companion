using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using ESPresense.Converters;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Models;

public class Device
{
    public Device(string id, TimeSpan timeout)
    {
        Id = id;
        Timeout = timeout;
        HassAutoDiscovery.Add(new AutoDiscovery(this, "device_tracker"));
    }

    public override string ToString()
    {
        return $"{nameof(Id)}: {Id}";
    }

    public string Id { get; init; }
    public string? Name { get; set; }

    [JsonIgnore] public Point3D ReportedLocation { get; set; }

    [JsonConverter(typeof(DeviceToNodeConverter))]
    public ConcurrentDictionary<string, DeviceToNode> Nodes { get; } = new(comparer: StringComparer.OrdinalIgnoreCase);

    [JsonConverter(typeof(RoomConverter))] public Room? Room => BestScenario?.Room;

    [JsonConverter(typeof(FloorConverter))] public Floor? Floor => BestScenario?.Floor;

    public int? Confidence => BestScenario?.Confidence;

    public double? Scale => BestScenario?.Scale;

    public int? Fixes => BestScenario?.Fixes;

    public DateTime? LastHit => BestScenario?.LastHit ?? Nodes.Values.Max(a => a.LastHit);

    [JsonIgnore] public bool Check { get; set; }
    [JsonIgnore] public bool Track { get; set; }

    [JsonIgnore] public Scenario? BestScenario { get; set; }
    [JsonIgnore] public IList<Scenario> Scenarios { get; } = new List<Scenario>();

    [JsonConverter(typeof(Point3DConverter))]
    public Point3D? Location => BestScenario?.Location;

    [JsonIgnore] public DateTime? LastCalculated { get; set; }

    [JsonIgnore] public IList<AutoDiscovery> HassAutoDiscovery { get; set; } = new List<AutoDiscovery>();
    [JsonIgnore] public string? ReportedState { get; set; }
    [JsonConverter(typeof(TimeSpanMillisConverter))]
    public TimeSpan Timeout { get; set; }

    public IEnumerable<KeyValuePair<string, string>> GetDetails()
    {
        yield return new KeyValuePair<string, string>("Best Scenario", $"{BestScenario?.Name}");

        var scenarios = Scenarios.OrderByDescending(s => s.Probability).ToArray();
        foreach (var s in scenarios)
        {
            yield return new KeyValuePair<string, string>($"{s.Name} Probability", $"{s.Probability:F4}");
            yield return new KeyValuePair<string, string>($"{s.Name} Room", $"{s.Room}");
            yield return new KeyValuePair<string, string>($"{s.Name} Confidence", $"{s.Confidence}");
            yield return new KeyValuePair<string, string>($"{s.Name} Fixes", $"{s.Fixes}");
            yield return new KeyValuePair<string, string>($"{s.Name} Error", $"{s.Error}");
            yield return new KeyValuePair<string, string>($"{s.Name} Iterations", $"{s.Iterations}");
            yield return new KeyValuePair<string, string>($"{s.Name} Scale", $"{s.Scale}");
            yield return new KeyValuePair<string, string>($"{s.Name} ReasonForExit", $"{s.ReasonForExit}");
        }

        var deviceNodes = Nodes.Values.Where(dn => dn.Node != null).OrderBy(dn => dn.Distance).ToList();
        foreach (var dn in deviceNodes)
        {
            yield return new KeyValuePair<string, string>($"{dn.Node?.Name} Rssi/@1m", $"{dn.Rssi}/{dn.RefRssi}");
            yield return new KeyValuePair<string, string>($"{dn.Node?.Name} Distance", $"{dn.Distance}");
            yield return new KeyValuePair<string, string>($"{dn.Node?.Name} Hits", $"{dn.Hits}");
            yield return new KeyValuePair<string, string>($"{dn.Node?.Name} Last Hit", $"{dn.LastHit?.ToLocalTime():s}");
        }

        foreach (var s in scenarios)
        {
            yield return new KeyValuePair<string, string>($"{s.Name} X", $"{s.Location.X:##.000}");
            yield return new KeyValuePair<string, string>($"{s.Name} Y", $"{s.Location.Y:##.000}");
            yield return new KeyValuePair<string, string>($"{s.Name} Z", $"{s.Location.Z:##.000}");
        }
    }
}
