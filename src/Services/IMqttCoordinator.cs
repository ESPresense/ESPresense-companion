using ESPresense.Events;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;

namespace ESPresense.Services;

public interface IMqttCoordinator
{
    // Events
    event Func<DeviceSettingsEventArgs, Task>? DeviceConfigReceivedAsync;
    event Func<DeviceMessageEventArgs, Task>? DeviceMessageReceivedAsync;
    event Func<MqttApplicationMessageReceivedEventArgs, Task>? MqttMessageReceivedAsync;
    event Func<NodeSettingReceivedEventArgs, Task>? NodeSettingReceivedAsync;
    event Func<NodeTelemetryReceivedEventArgs, Task>? NodeTelemetryReceivedAsync;
    event Func<NodeTelemetryRemovedEventArgs, Task>? NodeTelemetryRemovedAsync;
    event Func<NodeStatusReceivedEventArgs, Task>? NodeStatusReceivedAsync;
    event Func<NodeStatusRemovedEventArgs, Task>? NodeStatusRemovedAsync;
    event EventHandler? MqttMessageMalformed;
    event EventHandler<PreviousDeviceDiscoveredEventArgs>? PreviousDeviceDiscovered;
    event Func<DeviceAttributesEventArgs, Task>? DeviceAttributesReceivedAsync;

    // Methods
    Task EnqueueAsync(string topic, string? payload, bool retain = false);
}
