using ESPresense.Models;

namespace ESPresense.Controllers;

public class GlobalEventDispatcher(DeviceHistoryStore dh)
{
    public event EventHandler<NodeStateEventArgs>? NodeStateChanged;
    public event EventHandler<DeviceEventArgs>? DeviceStateChanged;
    public event EventHandler<CalibrationEventArgs>? CalibrationChanged;

    public void OnNodeStateChanged(NodeState state)
    {
        NodeStateChanged?.Invoke(this, new NodeStateEventArgs(state));
    }

    public async Task OnDeviceChanged(Device device)
    {
        foreach (var ds in device.Scenarios)
        {
            if (ds.Confidence == 0) continue;
            await dh.Add(new DeviceHistory { Id = device.Id, When = DateTime.UtcNow, X = ds.Location.X, Y = ds.Location.Y, Z = ds.Location.Z, Confidence = ds.Confidence ?? 0, Fixes = ds.Fixes ?? 0, Scenario = ds.Name, Best = ds == device.BestScenario });
        }
        DeviceStateChanged?.Invoke(this, new DeviceEventArgs(device));
    }

    public void OnCalibrationChanged(Calibration calibration)
    {
        CalibrationChanged?.Invoke(this, new CalibrationEventArgs(calibration));
    }
}

public class NodeStateEventArgs(NodeState state) : EventArgs
{
    public NodeState NodeState { get; } = state;
}

public class DeviceEventArgs(Device device) : EventArgs
{
    public Device Device { get; } = device;
}

public class CalibrationEventArgs(Calibration calibration) : EventArgs
{
    public Calibration Calibration { get; } = calibration;
}