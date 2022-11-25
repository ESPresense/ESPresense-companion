using System.Collections.Concurrent;
using ESPresense.Models;

namespace ESPresense;

public class State
{
    public ConcurrentDictionary<string, Node> Nodes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, Device> Devices { get; } = new(StringComparer.OrdinalIgnoreCase);
}