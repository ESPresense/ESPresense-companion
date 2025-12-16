using ESPresense.Models;

namespace ESPresense.Locators;

internal class NearestNode : ILocate
{
    private readonly Device _device;
    private readonly State _state;

    public NearestNode(Device device, State state)
    {
        _device = device;
        _state = state;
    }

    public bool Locate(Scenario scenario)
    {
        var device = _device;
        var nodes = device.Nodes.Values.Where(a => a.Current).OrderBy(a => a.Distance).ToArray();
        var nearest = nodes.FirstOrDefault();
        if (nearest == null) return false;

        scenario.UpdateLocation(nearest.Node!.Location);
        scenario.Confidence = 1;
        scenario.Fixes = 1;
        scenario.Floor = nearest.Node.Floors?.FirstOrDefault();
        scenario.Room = null;

        return true;
    }
}