using ESPresense.Models;

namespace ESPresense.Locators;

internal class NearestNode : ILocate
{
    private readonly Device _device;

    public NearestNode(Device device)
    {
        _device = device;
    }

    public bool Locate(Scenario scenario)
    {
        return false;
    }
}