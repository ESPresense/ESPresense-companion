using System.Collections.Concurrent;
using ESPresense.Services;

namespace ESPresense.Models;

public class State
{
    public State(ConfigLoader cl)
    {
        void LoadConfig(Config c)
        {
            foreach (var node in c.Nodes ?? Enumerable.Empty<ConfigNode>()) Nodes.GetOrAdd(node.GetId(), a => new Node()).Update(c, node);
            foreach (var floor in c.Floors ?? Enumerable.Empty<ConfigFloor>()) Floors.GetOrAdd(floor.GetId(), a => new Floor()).Update(c, floor);
        }

        cl.ConfigChanged += (_, args) => { LoadConfig(args); };
        if (cl.Config != null) LoadConfig(cl.Config);
    }

    public ConcurrentDictionary<string, Node> Nodes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, Device> Devices { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, Floor> Floors { get; } = new(StringComparer.OrdinalIgnoreCase);
}

