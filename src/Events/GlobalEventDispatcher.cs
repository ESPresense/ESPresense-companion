using ESPresense.Events;
using ESPresense.Models;

namespace ESPresense.Controllers;

public class GlobalEventDispatcher()
{
    public event EventHandler<DeviceMessageEventArgs> DeviceMessageReceived;
    public event EventHandler<NodeStateEventArgs>? NodeStateChanged;
    public event EventHandler<DeviceEventArgs>? DeviceStateChanged;
    public event EventHandler<CalibrationEventArgs>? CalibrationChanged;
    public event EventHandler<DeviceRemovedEventArgs>? DeviceRemoved;

    public void OnNodeStateChanged(NodeState state)
    {
        NodeStateChanged?.Invoke(this, new NodeStateEventArgs(state));
    }

    public void OnDeviceChanged(Device device, bool trackChanged)
    {
        DeviceStateChanged?.Invoke(this, new DeviceEventArgs(device, trackChanged));
    }

    public void OnCalibrationChanged(Calibration calibration)
    {
        CalibrationChanged?.Invoke(this, new CalibrationEventArgs(calibration));
    }

    public void OnDeviceMessageReceived(DeviceMessageEventArgs e)
    {
        DeviceMessageReceived?.Invoke(this, e);
    }

    public void OnDeviceRemoved(string deviceId)
    {
        DeviceRemoved?.Invoke(this, new DeviceRemovedEventArgs(deviceId));
    }
}

public class NodeStateEventArgs(NodeState state) : EventArgs
{
    public NodeState NodeState { get; } = state;
}

public class DeviceEventArgs(Device device, bool trackChanged) : EventArgs
{
    public Device Device { get; } = device;
    public bool TrackChanged { get; } = trackChanged;
}

public class CalibrationEventArgs(Calibration calibration) : EventArgs
{
    public Calibration Calibration { get; } = calibration;
}

public class DeviceRemovedEventArgs(string deviceId) : EventArgs
{
    public string DeviceId { get; } = deviceId;
}
