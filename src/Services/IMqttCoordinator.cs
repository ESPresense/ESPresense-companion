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

    /// <summary>
    /// Enqueues an MQTT message for delivery (or logs intent when coordinator is in read-only mode).
    /// </summary>
    /// <param name="topic">MQTT topic to publish to.</param>
    /// <param name="payload">Message payload; may be null to clear retained messages for the topic.</param>
    /// <param name="retain">If true, the broker will retain the message.</param>
    /// <returns>A task that completes when the message has been enqueued or the intent has been logged.</returns>
    Task EnqueueAsync(string topic, string? payload, bool retain = false);

    /// <summary>
    /// Waits for MQTT connection to be established and ready (connected with all subscriptions active).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to cancel the wait operation.</param>
    /// <returns>A task that completes when MQTT is connected and ready, or throws if connection fails.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled or connection is invalidated due to configuration change.</exception>
    Task WaitForConnectionAsync(CancellationToken cancellationToken = default);
}
