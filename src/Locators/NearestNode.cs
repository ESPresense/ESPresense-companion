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
        return false;
    }
}