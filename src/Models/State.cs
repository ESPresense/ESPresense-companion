using System.Collections.Concurrent;
using DotNet.Globbing;
using DotNet.Globbing.Token;
using ESPresense.Extensions;
using ESPresense.Locators;
using ESPresense.Services;
using ESPresense.Weighting;
using Serilog;

namespace ESPresense.Models;

public class State
{
    public State(ConfigLoader cl)
    {
        void LoadConfig(Config c)
        {
            Config = c;
            foreach (var cf in c.Floors ?? Enumerable.Empty<ConfigFloor>()) Floors.GetOrAdd(cf.GetId()).Update(c, cf);
            foreach (var cn in c.Nodes ?? Enumerable.Empty<ConfigNode>()) Nodes.GetOrAdd(cn.GetId(), a => new Node(cn.GetId())).Update(c, cn, GetFloorsByIds(cn.Floors));

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

            var w = c?.Weighting;
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

    public OptimizationSnapshot TakeOptimizationSnapshot()
    {
        Dictionary<string, OptNode> nodes = new();
        var os = new OptimizationSnapshot();
        foreach (var (txId, txNode) in Nodes)
            foreach (var (rxId, meas) in txNode.RxNodes)
            {
                var tx = nodes.GetOrAdd(txId, a => new OptNode { Id = txId, Name = txNode.Name, Location = txNode.Location });
                var rx = nodes.GetOrAdd(rxId, a => new OptNode { Id = rxId, Name = meas.Rx!.Name, Location = meas.Rx.Location });
                os.Measures.Add(new Measure()
                {
                    Current = meas.Current,
                    Distance = meas.Distance,
                    RefRssi = meas.RefRssi,
                    Rssi = meas.Rssi,
                    Tx = tx,
                    Rx = rx,
                });
            }

        if (OptimizationSnaphots.Count > Config?.Optimization.MaxSnapshots) OptimizationSnaphots.RemoveAt(0);
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
        var nealderMead = Config?.Locators?.NealderMead;
        var nadarayaWatson = Config?.Locators?.NadarayaWatson;
        var nearestNode = Config?.Locators?.NearestNode;

        if ((nealderMead?.Enabled ?? false) || (nadarayaWatson?.Enabled ?? false) || (nearestNode?.Enabled ?? false))
        {
            if (nealderMead?.Enabled ?? false)
                foreach (var floor in GetFloorsByIds(nealderMead?.Floors))
                    yield return new Scenario(Config, new NelderMeadMultilateralizer(device, floor, this), floor.Name);

            if (nadarayaWatson?.Enabled ?? false)
                foreach (var floor in GetFloorsByIds(nadarayaWatson?.Floors))
                    yield return new Scenario(Config, new NadarayaWatsonMultilateralizer(device, floor, this), floor.Name);

            if (nearestNode?.Enabled ?? false)
                yield return new Scenario(Config, new NearestNode(device), "NearestNode");
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

        if (ConfigDeviceById.TryGetValue(device.Id, out var cdById))
        {
            if (!string.IsNullOrWhiteSpace(cdById.Name))
                device.Name = cdById.Name;
            return true;
        }
        if (!string.IsNullOrWhiteSpace(device.Name) && ConfigDeviceByName.TryGetValue(device.Name, out _))
            return true;
        if (!string.IsNullOrWhiteSpace(device.Id) && IdsToTrack.Any(a => a.IsMatch(device.Id)))
            return true;
        if (!string.IsNullOrWhiteSpace(device.Name) && NamesToTrack.Any(a => a.IsMatch(device.Name)))
            return true;
        return false;
    }

    private bool IsExcluded(Device device)
    {
        return Config?.ExcludeDevices.Any(d =>
            (!string.IsNullOrWhiteSpace(d.Id) && !string.IsNullOrWhiteSpace(device.Id) && Glob.Parse(d.Id).IsMatch(device.Id)) ||
            (!string.IsNullOrWhiteSpace(d.Name) && !string.IsNullOrWhiteSpace(device.Name) && Glob.Parse(d.Name).IsMatch(device.Name))
        ) ?? false;
    }
}
