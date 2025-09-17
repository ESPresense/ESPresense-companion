using System.Collections.Concurrent;
using DotNet.Globbing;
using DotNet.Globbing.Token;
using ESPresense.Extensions;
using ESPresense.Locators;
using ESPresense.Services;
using ESPresense.Weighting;
using MathNet.Spatial.Euclidean;
using System.Linq;
using Serilog;

namespace ESPresense.Models;

public class State
{
    private readonly NodeTelemetryStore _nts;
    private readonly DeviceSettingsStore _deviceSettingsStore;

    /// <summary>
    /// Initializes a new State, wiring telemetry and configuration handling.
    /// </summary>
    /// <remarks>
    /// Subscribes to the provided ConfigLoader's ConfigChanged event and loads the current configuration if present.
    /// Loading the configuration populates Floors, Nodes, device tracking structures (by literal and pattern globs),
    /// selects the locators' weighting strategy, and marks existing Devices for checking.
    /// </remarks>
    public State(ConfigLoader cl, NodeTelemetryStore nts, DeviceSettingsStore deviceSettingsStore)
    {
        _nts = nts;
        _deviceSettingsStore = deviceSettingsStore;
        void LoadConfig(Config c)
        {
            Config = c;
            foreach (var cf in c.Floors ?? Enumerable.Empty<ConfigFloor>()) Floors.GetOrAdd(cf.GetId()).Update(c, cf);
            foreach (var cn in c.Nodes ?? Enumerable.Empty<ConfigNode>()) Nodes.GetOrAdd(cn.GetId(), a => new Node(cn.GetId(), NodeSourceType.Config)).Update(c, cn, GetFloorsByIds(cn.Floors));

            var idsToTrack = new List<Glob>();
            var configDeviceById = new ConcurrentDictionary<string, ConfigDevice>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in c.Devices ?? Enumerable.Empty<ConfigDevice>())
                if (!string.IsNullOrWhiteSpace(d.Id))
                {
                    var glob = Glob.Parse(d.Id);
                    if (glob.Tokens.All(a => a is LiteralToken))
                        configDeviceById.GetOrAdd(d.Id, a => d);
                    else
                        idsToTrack.Add(glob);
                }

            var namesToTrack = new List<Glob>();
            var configDeviceByName = new ConcurrentDictionary<string, ConfigDevice>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in c.Devices ?? Enumerable.Empty<ConfigDevice>())
                if (!string.IsNullOrWhiteSpace(d.Name))
                {
                    var glob = Glob.Parse(d.Name);
                    if (glob.Tokens.All(a => a is LiteralToken))
                        configDeviceByName.GetOrAdd(d.Name, a => d);
                    else
                        namesToTrack.Add(glob);
                }

            IdsToTrack = idsToTrack;
            ConfigDeviceById = configDeviceById;
            NamesToTrack = namesToTrack;
            ConfigDeviceByName = configDeviceByName;

            var w = c?.Locators?.NelderMead?.Weighting;
            Weighting = w?.Algorithm switch
            {
                "equal" => new EqualWeighting(),
                "gaussian" => new GaussianWeighting(w?.Props),
                "exponential" => new ExponentialWeighting(w?.Props),
                _ => new GaussianWeighting(w?.Props),
            };
            foreach (var device in Devices.Values) device.Check = true;
        }

        cl.ConfigChanged += (_, args) => { LoadConfig(args); };
        if (cl.Config != null) LoadConfig(cl.Config);
    }

    public Config? Config;

    public ConcurrentDictionary<string, Node> Nodes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, Device> Devices { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, Floor> Floors { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, ConfigDevice> ConfigDeviceByName { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, ConfigDevice> ConfigDeviceById { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Glob> IdsToTrack { get; private set; } = new();
    public List<Glob> NamesToTrack { get; private set; } = new();
    public List<OptimizationSnapshot> OptimizationSnaphots { get; } = new();
    public IWeighting? Weighting { get; set; }
    public OptimizerState OptimizerState { get; set; } = new();

    /// <summary>
    /// Creates an optimization snapshot from current node measurements and purges expired snapshots.
    /// </summary>
    /// <remarks>
    /// Iterates through all nodes and their receiver measurements, recording only those marked as current into a new snapshot
    /// timestamped with the current UTC time. It then removes any snapshots older than the configured expiration threshold
    /// (defaulting to 5 minutes) before adding the new snapshot to the collection.
    /// </remarks>
    /// <returns>The newly created optimization snapshot containing active measurements.</returns>
    public OptimizationSnapshot TakeOptimizationSnapshot()
    {
        Dictionary<string, OptNode> nodes = new();
        var os = new OptimizationSnapshot
        {
            Timestamp = DateTime.UtcNow
        };
        foreach (var (txId, txNode) in Nodes)
            foreach (var (rxId, meas) in txNode.RxNodes)
            {
                var tx = nodes.GetOrAdd(txId, a => new OptNode { Id = txId, Name = txNode.Name, Location = txNode.Location });
                var rx = nodes.GetOrAdd(rxId, a => new OptNode { Id = rxId, Name = meas.Rx!.Name, Location = meas.Rx.Location });
                if (meas.Current)
                {
                    os.Measures.Add(new Measure()
                    {
                        Distance = meas.Distance,
                        DistVar = meas.DistVar,
                        Rssi = meas.Rssi,
                        RssiRxAdj = meas.RssiRxAdj,
                        RssiVar = meas.RssiVar,
                        RefRssi = meas.RefRssi,
                        Tx = tx,
                        Rx = rx,
                    });
                }
            }

        foreach (var device in Devices.Values.Where(d => d.Anchor != null))
            foreach (var (rxId, dn) in device.Nodes)
            {
                var tx = nodes.GetOrAdd(device.Id, a => new OptNode { Id = device.Id, Name = device.Name, Location = device.Anchor!.Value });
                var rx = nodes.GetOrAdd(rxId, a => new OptNode { Id = rxId, Name = dn.Node.Name, Location = dn.Node.Location });
                if (dn.Current)
                {
                    os.Measures.Add(new Measure
                    {
                        Distance = dn.Distance,
                        DistVar = dn.DistVar,
                        Rssi = dn.Rssi,
                        RssiVar = dn.RssiVar,
                        RefRssi = dn.RefRssi,
                        Tx = tx,
                        Rx = rx,
                    });
                }
            }

        // Remove expired snapshots by time
        var expiryMinutes = Config?.Optimization?.KeepSnapshotMins ?? 5;
        var expiryThreshold = DateTime.UtcNow.AddMinutes(-expiryMinutes);
        OptimizationSnaphots.RemoveAll(s => s.Timestamp < expiryThreshold);
        OptimizationSnaphots.Add(os);

        return os;
    }

    IEnumerable<Floor> GetFloorsByIds(string[]? floorIds)
    {
        if (floorIds == null)
        {
            foreach (var floor in Floors.Values)
                yield return floor;
        }
        else
            foreach (var floorId in floorIds)
                if (Floors.TryGetValue(floorId, out var floor))
                    yield return floor;
    }

    public IEnumerable<Scenario> GetScenarios(Device device)
    {
        if (device.Anchor != null)
        {
            var anchor = device.Anchor.Value;
            var floor = Floors.Values.FirstOrDefault(f => f.Contained(anchor.Z));
            var room = floor?.Rooms.Values.FirstOrDefault(r => r.Polygon?.EnclosesPoint(anchor.ToPoint2D()) ?? false);
            var scenario = new Scenario(Config, new AnchorLocator(anchor), "Anchor")
            {
                Floor = floor,
                Room = room,
                Confidence = 100
            };
            yield return scenario;
            yield break;
        }
        var nelderMead = Config?.Locators?.NelderMead;
        var nadarayaWatson = Config?.Locators?.NadarayaWatson;
        var nearestNode = Config?.Locators?.NearestNode;

        if ((nelderMead?.Enabled ?? false) || (nadarayaWatson?.Enabled ?? false) || (nearestNode?.Enabled ?? false))
        {
            if (nelderMead?.Enabled ?? false)
                foreach (var floor in GetFloorsByIds(nelderMead?.Floors))
                    yield return new Scenario(Config, new NelderMeadMultilateralizer(device, floor, this), floor.Name);

            if (nadarayaWatson?.Enabled ?? false)
                foreach (var floor in GetFloorsByIds(nadarayaWatson?.Floors))
                    yield return new Scenario(Config, new NadarayaWatsonMultilateralizer(device, floor, this, _nts), floor.Name);

            if (nearestNode?.Enabled ?? false)
                yield return new Scenario(Config, new NearestNode(device, this), "NearestNode");
        }
        else
        {
            Log.Warning("No locators enabled, using default NelderMead");
            foreach (var floor in Floors.Values)
                yield return new Scenario(Config, new NelderMeadMultilateralizer(device, floor, this), floor.Name);
        }
    }

    public bool ShouldTrack(Device device)
    {
        if (IsExcluded(device))
            return false;

        bool shouldTrack = false;

        // Check DeviceSettings for anchor coordinates and tracking
        var deviceSettings = _deviceSettingsStore.Get(device.Id);
        if (deviceSettings != null)
        {
            if (!string.IsNullOrWhiteSpace(deviceSettings.Name))
                device.Name = deviceSettings.Name;
            if (deviceSettings.X.HasValue && deviceSettings.Y.HasValue && deviceSettings.Z.HasValue)
                device.Anchor = new Point3D(deviceSettings.X.Value, deviceSettings.Y.Value, deviceSettings.Z.Value);
            shouldTrack = true;
        }

        if (ConfigDeviceById.TryGetValue(device.Id, out var cdById))
        {
            if (!string.IsNullOrWhiteSpace(cdById.Name))
                device.Name = cdById.Name;
            if (cdById.Point?.Length >= 3)
                device.Anchor = new Point3D(cdById.Point[0], cdById.Point[1], cdById.Point[2]);
            shouldTrack = true;
        }
        if (!string.IsNullOrWhiteSpace(device.Name) && ConfigDeviceByName.TryGetValue(device.Name, out var cdByName))
        {
            if (cdByName.Point?.Length >= 3)
                device.Anchor = new Point3D(cdByName.Point[0], cdByName.Point[1], cdByName.Point[2]);
            shouldTrack = true;
        }
        if (!string.IsNullOrWhiteSpace(device.Id) && IdsToTrack.Any(a => a.IsMatch(device.Id)))
            shouldTrack = true;
        if (!string.IsNullOrWhiteSpace(device.Name) && NamesToTrack.Any(a => a.IsMatch(device.Name)))
            shouldTrack = true;

        return shouldTrack;
    }

    private bool IsExcluded(Device device)
    {
        return Config?.ExcludeDevices.Any(d =>
            (!string.IsNullOrWhiteSpace(d.Id) && !string.IsNullOrWhiteSpace(device.Id) && Glob.Parse(d.Id).IsMatch(device.Id)) ||
            (!string.IsNullOrWhiteSpace(d.Name) && !string.IsNullOrWhiteSpace(device.Name) && Glob.Parse(d.Name).IsMatch(device.Name))
        ) ?? false;
    }
}
