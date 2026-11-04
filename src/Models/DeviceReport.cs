using ESPresense.Extensions;
using System.Text.Json;
using ESPresense.Utils;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Models;

/// <summary>
/// What the outside world sees for a device: the value of the state topic
/// (espresense/companion/{id}) and the device_tracker attributes JSON
/// (espresense/companion/{id}/attributes). One shape for anchored and located devices.
/// </summary>
/// <remarks>
/// The attributes JSON is a public contract with Home Assistant; DevicePublishContractTests pins it.
/// <see cref="Location"/> is null when there is nothing to place (a located device with no current
/// scenario): the state still goes out, the attributes don't.
/// </remarks>
public sealed record DeviceReport(
    string State,
    Point3D? Location,
    double? Latitude,
    double? Longitude,
    double? Elevation,
    int? Confidence,
    int? Fixes,
    string? BestScenario,
    DateTime? LastSeen)
{
    public const string NotHome = "not_home";
    public const string AnchoredScenario = "Anchored";

    public static DeviceReport From(Device device, Scenario? best, ConfigGps? gps)
    {
        if (device.Anchor is { } anchor)
        {
            // The anchor fixes where the device is, not whether it is here: once no node hears it, it's away.
            var present = device.Nodes.Values.Any(dn => dn.Current);
            var state = present ? Place(device) : NotHome;
            // Fixes stays null rather than 0: the payload has never carried fixes for an anchored device.
            return Build(state, anchor.Location, 100, null, AnchoredScenario, device.LastSeen, gps);
        }

        if (best == null)
            return new DeviceReport(NotHome, null, null, null, null, null, null, null, null);

        return Build(Place(device), device.Location ?? new Point3D(), best.Confidence, best.Fixes, best.Name, device.LastSeen, gps);
    }

    private static string Place(Device device) => device.Room?.Name ?? device.Floor?.Name ?? NotHome;

    private static DeviceReport Build(string state, Point3D location, int? confidence, int? fixes, string? bestScenario, DateTime? lastSeen, ConfigGps? gps)
    {
        // Only include GPS coordinates if reporting is enabled
        var report = gps?.Report == true;
        var (lat, lon) = report ? gps.Add(location.X, location.Y) : (null, null);
        var elevation = report ? location.Z + gps!.Elevation : null;
        return new DeviceReport(state, location, lat, lon, elevation, confidence, fixes, bestScenario, lastSeen);
    }

    /// <summary>The attributes payload, or null when there is no location to report.</summary>
    public string? AttributesJson()
    {
        if (Location is not { } location) return null;

        // Property names, order and types are the wire format. JsonDefaults.Mqtt reproduces the Newtonsoft
        // NullIgnore bytes this payload always had, including dropping zero doubles.
        return JsonSerializer.Serialize(new
        {
            source_type = "espresense",
            latitude = Latitude,
            longitude = Longitude,
            elevation = Elevation,
            x = location.X,
            y = location.Y,
            z = location.Z,
            confidence = Confidence,
            fixes = Fixes,
            best_scenario = BestScenario,
            last_seen = LastSeen
        }, JsonDefaults.Mqtt);
    }
}
