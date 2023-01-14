using System.Collections.Concurrent;
using ESPresense.Services;

namespace ESPresense.Models;

public class State
{
    public State(ConfigLoader cl)
    {
        IEnumerable<Floor> GetFloorsByIds(string[]? floorIds)
        {
            if (floorIds == null) yield break;
            foreach (var floorId in floorIds)
                if (Floors.TryGetValue(floorId, out var floor))
                    yield return floor;
        }
        
        ConcurrentDictionary<string, ConfigDevice> GetConfigDeviceById(Config c)
        {
            ConcurrentDictionary<string, ConfigDevice> devices = new(StringComparer.OrdinalIgnoreCase);
            foreach (var device in c.Devices ?? Enumerable.Empty<ConfigDevice>())
                if (!string.IsNullOrWhiteSpace(device.Id))
                    devices.GetOrAdd(device.Id, a => device);
            return devices;
        }

        ConcurrentDictionary<string, ConfigDevice> GetConfigDeviceByName(Config c)
        {
            ConcurrentDictionary<string, ConfigDevice> devices = new(StringComparer.OrdinalIgnoreCase);
            foreach (var device in c.Devices ?? Enumerable.Empty<ConfigDevice>())
                if (!string.IsNullOrWhiteSpace(device.Name))
                    devices.GetOrAdd(device.Name, a => device);
            return devices;
        }

        void LoadConfig(Config c)
        {
            Config = c;
            foreach (var floor in c.Floors ?? Enumerable.Empty<ConfigFloor>()) Floors.GetOrAdd(floor.GetId(), a => new Floor()).Update(c, floor);
            foreach (var node in c.Nodes ?? Enumerable.Empty<ConfigNode>()) Nodes.GetOrAdd(node.GetId(), a => new Node()).Update(c, node, GetFloorsByIds(node.Floors));

            ConfigDeviceById = GetConfigDeviceById(c);
            ConfigDeviceByName = GetConfigDeviceByName(c);
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
}

