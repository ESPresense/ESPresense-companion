using ESPresense.Events;
using MQTTnet;

namespace ESPresense.Services;

public interface IMqttCoordinator
{
    // Properties
    string DiscoveryTopic { get; }

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
