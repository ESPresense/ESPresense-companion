using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Serialization;
using ESPresense.Converters;
using ESPresense.Extensions;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Models;

public class Device
{
    private DateTime? _lastSeen;

    // Kalman filter for location smoothing
    private readonly KalmanLocation _kalmanLocation = new();

    /// <summary>
    /// Access to the device's Kalman filter for prediction
    /// </summary>
    [JsonIgnore] public KalmanLocation KalmanFilter => _kalmanLocation;

    public Device(string id, string? discoveryId, TimeSpan timeout)
    {
        Id = id;
        Timeout = timeout;
        HassAutoDiscovery.Add(new AutoDiscovery("device_tracker", this, discoveryId, "bluetooth"));
    }

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.Append($"{nameof(Id)}: {Id}");
        if (!string.IsNullOrEmpty(Name)) sb.Append($", {nameof(Name)}: {Name}");
        if (!Track) sb.Append($", {nameof(Track)}: {Track}");
        return sb.ToString();
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

    public int? Fixes => Nodes.Values.Count(dn => dn.Current);

    public DateTime? LastSeen
    {
        get
        {
            var lastSeen =  BestScenario?.LastHit ?? Nodes.Values.Max(a => a.LastHit);
            if (_lastSeen == null || lastSeen > _lastSeen) _lastSeen = lastSeen;
            return _lastSeen;
        }
        set
        {
            _lastSeen = value;
        }
    }

    [JsonIgnore] public bool Check { get; set; }
    [JsonIgnore] public bool Track { get; set; }

    [JsonIgnore] public Scenario? BestScenario { get; set; }
    [JsonIgnore] public IList<Scenario> Scenarios { get; } = new List<Scenario>();

    [JsonIgnore]
    public ConcurrentDictionary<string, double> BayesianProbabilities { get; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public ConcurrentDictionary<string, AutoDiscovery> BayesianDiscoveries { get; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonConverter(typeof(Point3DConverter))]
    public Point3D? Location
    {
        get
        {
            // If no best scenario, return null
            if (BestScenario == null) return null;

            // Return the smoothed location
            return _kalmanLocation.Location;
        }
    }

    [JsonIgnore] public DateTime? LastCalculated { get; set; }

    [JsonIgnore] public IList<AutoDiscovery> HassAutoDiscovery { get; set; } = new List<AutoDiscovery>();
    [JsonIgnore] public string? ReportedState { get; set; }
    [JsonConverter(typeof(TimeSpanMillisConverter))]
    public TimeSpan Timeout { get; set; }

    [JsonIgnore]
    public int? ConfiguredRefRssi { get; set; }

    [JsonPropertyName("rssi@1m")]
    public double? RefRssi
    {
        get
        {
            // Only return configured values for calibration status
            // Use HasValue to distinguish between null (unconfigured) and 0 (valid 0 dBm)
            return ConfiguredRefRssi.HasValue ? ConfiguredRefRssi.Value : null;
        }
    }

    [JsonPropertyName("measuredRssi@1m")]
    public double? MeasuredRefRssi
    {
        get
        {
            var currentNodes = Nodes.Values.Where(dn => dn.Current).ToList();
            if (currentNodes.Count == 0) return null;
            
            var refRssiValues = currentNodes.Where(dn => dn.RefRssi != 0).Select(dn => dn.RefRssi).ToList();
            return refRssiValues.Count > 0 ? refRssiValues.Average() : null;
        }
    }

    /// <summary>
    /// Updates the device's location using Kalman filtering for smooth transitions
    /// </summary>
    /// <param name="newLocation">The new location from the best scenario</param>
    /// <param name="confidence">The confidence level of the new location</param>
    public void UpdateLocation(Point3D newLocation)
    {
        _kalmanLocation.Update(newLocation);
    }

    public void ResetBayesianState()
    {
        foreach (var discovery in BayesianDiscoveries.Values.ToList())
        {
            HassAutoDiscovery.Remove(discovery);
        }

        BayesianDiscoveries.Clear();
        BayesianProbabilities.Clear();
    }

    public virtual IEnumerable<KeyValuePair<string, string>> GetDetails()
    {
        yield return new KeyValuePair<string, string>("Best Scenario", $"{BestScenario?.Name}");

        var scenarios = Scenarios.OrderByDescending(s => s.Probability).ToArray();
        foreach (var s in scenarios)
        {
            yield return new KeyValuePair<string, string>($"{s.Name} Probability", $"{s.Probability:F4}");
            yield return new KeyValuePair<string, string>($"{s.Name} Room", $"{s.Room}");
            yield return new KeyValuePair<string, string>($"{s.Name} Confidence", $"{s.Confidence}");
            yield return new KeyValuePair<string, string>($"{s.Name} Pearson Correlation", $"{s.PearsonCorrelation:F4}");
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
