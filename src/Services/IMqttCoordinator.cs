using System.Threading;
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
    /// Publishes an MQTT message and awaits the publish (there is no outbound queue despite the name;
    /// the call completes when the message has been sent, or throws). In read-only mode the message is
    /// not sent and the intent is logged instead.
    /// </summary>
    /// <param name="topic">MQTT topic to publish to.</param>
    /// <param name="payload">Message payload; may be null to clear retained messages for the topic.</param>
    /// <param name="retain">If true, the broker will retain the message.</param>
    /// <returns>A task that completes when the message has been published or the intent has been logged.</returns>
    Task EnqueueAsync(string topic, string? payload, bool retain = false);

    /// <summary>
    /// Publishes an MQTT message like <see cref="EnqueueAsync"/> but never throws (except for cancellation).
    /// Logs errors and returns success/failure status instead of propagating exceptions.
    /// Use this for best-effort publishes (telemetry, status updates) that shouldn't crash background services.
    /// </summary>
    /// <param name="topic">MQTT topic to publish to.</param>
    /// <param name="payload">Message payload; may be null to clear retained messages for the topic.</param>
    /// <param name="retain">If true, the broker will retain the message.</param>
    /// <returns>True if the message was published (or logged in read-only mode), false if an error occurred.</returns>
    Task<bool> TryEnqueueAsync(string topic, string? payload, bool retain = false);

    /// <summary>
    /// Waits for MQTT connection to be established and ready (connected with all subscriptions active).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to cancel the wait operation.</param>
    /// <returns>A task that completes when MQTT is connected and ready, or throws if connection fails.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled or connection is invalidated due to configuration change.</exception>
    Task WaitForConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears retained messages for the specified topic filter.
    /// </summary>
    /// <param name="topicFilter">MQTT topic filter to clear retained messages for.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <returns>A task that completes when the operation is complete.</returns>
    Task ClearRetainedAsync(string topicFilter, CancellationToken cancellationToken = default);
}
