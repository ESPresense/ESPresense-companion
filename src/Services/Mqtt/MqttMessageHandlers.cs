using ESPresense.Events;
using ESPresense.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;

namespace ESPresense.Services;

/// <summary>
/// The coordinator's public events as seen by the message handlers. <see cref="MqttCoordinator"/> implements it
/// so the events stay declared (and subscribed) on the facade; tests can supply their own.
/// A <c>null</c> delegate means "no subscribers": handlers use that to skip parsing, as the coordinator always did.
/// </summary>
internal interface IMqttEventSink
{
    Func<DeviceSettingsEventArgs, Task>? DeviceConfigReceived { get; }
    Func<DeviceMessageEventArgs, Task>? DeviceMessageReceived { get; }
    Func<NodeSettingReceivedEventArgs, Task>? NodeSettingReceived { get; }
    Func<NodeTelemetryReceivedEventArgs, Task>? NodeTelemetryReceived { get; }
    Func<NodeTelemetryRemovedEventArgs, Task>? NodeTelemetryRemoved { get; }
    Func<NodeStatusReceivedEventArgs, Task>? NodeStatusReceived { get; }
    Func<NodeStatusRemovedEventArgs, Task>? NodeStatusRemoved { get; }
    Func<NodeUpdateErrorEventArgs, Task>? NodeUpdateError { get; }
    Func<DeviceAttributesEventArgs, Task>? DeviceAttributesReceived { get; }

    void RaisePreviousDeviceDiscovered(PreviousDeviceDiscoveredEventArgs args);
}

/// <summary>Parses the payload of one message family and raises its typed event.</summary>
internal interface IMqttMessageHandler<in TRoute> where TRoute : MqttRoute
{
    Task HandleAsync(TRoute route, string? payload);
}

/// <summary>Awaits every subscriber of an async multicast event.</summary>
internal static class AsyncEvent
{
    /// <remarks>
    /// <c>await SomeEvent(args)</c> only awaits the <em>last</em> subscriber's task; earlier
    /// handlers run but their tasks (and exceptions) are unobserved. This helper awaits each
    /// handler so all subscribers complete and any failure surfaces. Handlers are invoked on
    /// the captured invocation list, so a subscriber that unsubscribes mid-dispatch does not
    /// skew the iteration, and exceptions from one handler do not stop the others.
    /// </remarks>
    public static Task InvokeAllAsync<T>(Func<T, Task>? handlers, T args, string eventName)
    {
        if (handlers == null)
            return Task.CompletedTask;

        var invocations = handlers.GetInvocationList();
        return AwaitAll(invocations, args, eventName);

        static async Task AwaitAll(Delegate[] list, T arg, string name)
        {
            List<Exception>? errors = null;
            foreach (var invocation in list)
            {
                try
                {
                    await ((Func<T, Task>)invocation)(arg).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    (errors ??= new()).Add(ex);
                }
            }

            if (errors is { Count: 1 })
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
            else if (errors is { Count: > 1 })
                throw new AggregateException($"One or more handlers for {name} failed", errors);
        }
    }
}

/// <summary>JSON payload parsing shared by the handlers: one place that turns parse failures into <see cref="MqttMessageProcessingException"/>.</summary>
internal static class MqttPayload
{
    /// <summary>
    /// Deserializes <paramref name="payload"/>; a <c>null</c> result or a <see cref="JsonException"/> becomes an
    /// <see cref="MqttMessageProcessingException"/> carrying the topic, payload and message type.
    /// </summary>
    public static T Deserialize<T>(MqttRoute route, string? payload, string messageType, string nullMessage, string parseFailedMessage) where T : class
    {
        T? result;
        try
        {
            result = JsonConvert.DeserializeObject<T>(payload ?? "");
        }
        catch (JsonException ex)
        {
            throw new MqttMessageProcessingException(parseFailedMessage, route.Topic, payload, messageType, ex);
        }

        return result ?? throw new MqttMessageProcessingException(nullMessage, route.Topic, payload, messageType);
    }
}

/// <summary>
/// Node telemetry. An empty payload is a cleared retained message and raises <c>NodeTelemetryRemovedAsync</c>;
/// otherwise the JSON is parsed (only if anyone is subscribed) and <c>NodeTelemetryReceivedAsync</c> is raised.
/// </summary>
internal sealed class NodeTelemetryHandler(IMqttEventSink sink) : IMqttMessageHandler<NodeTelemetryRoute>
{
    public async Task HandleAsync(NodeTelemetryRoute route, string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            await AsyncEvent.InvokeAllAsync(sink.NodeTelemetryRemoved, new NodeTelemetryRemovedEventArgs { NodeId = route.NodeId }, nameof(IMqttCoordinator.NodeTelemetryRemovedAsync));
            return;
        }

        var handlers = sink.NodeTelemetryReceived;
        if (handlers == null) return;

        var telemetry = MqttPayload.Deserialize<NodeTelemetry>(route, payload, "Telemetry", "Telemetry data was null after deserialization", "Failed to parse telemetry data");
        await AsyncEvent.InvokeAllAsync(handlers, new NodeTelemetryReceivedEventArgs { NodeId = route.NodeId, Payload = telemetry }, nameof(IMqttCoordinator.NodeTelemetryReceivedAsync));
    }
}

/// <summary>
/// Node availability. An empty payload is a cleared retained message (node removed) and raises
/// <c>NodeStatusRemovedAsync</c>; otherwise <c>NodeStatusReceivedAsync</c> with Online = (payload == "online").
/// </summary>
internal sealed class NodeStatusHandler(IMqttEventSink sink) : IMqttMessageHandler<NodeStatusRoute>
{
    public Task HandleAsync(NodeStatusRoute route, string? payload)
    {
        if (string.IsNullOrEmpty(payload))
            return AsyncEvent.InvokeAllAsync(sink.NodeStatusRemoved, new NodeStatusRemovedEventArgs { NodeId = route.NodeId }, nameof(IMqttCoordinator.NodeStatusRemovedAsync));

        return AsyncEvent.InvokeAllAsync(sink.NodeStatusReceived, new NodeStatusReceivedEventArgs { NodeId = route.NodeId, Online = payload == MqttTopics.Online }, nameof(IMqttCoordinator.NodeStatusReceivedAsync));
    }
}

/// <summary>A node's retained <c>update_error</c>; an empty payload means the error was cleared (Error = null).</summary>
internal sealed class NodeUpdateErrorHandler(IMqttEventSink sink) : IMqttMessageHandler<NodeUpdateErrorRoute>
{
    public Task HandleAsync(NodeUpdateErrorRoute route, string? payload) =>
        AsyncEvent.InvokeAllAsync(sink.NodeUpdateError, new NodeUpdateErrorEventArgs
        {
            NodeId = route.NodeId,
            Error = string.IsNullOrEmpty(payload) ? null : payload
        }, nameof(IMqttCoordinator.NodeUpdateErrorAsync));
}

/// <summary>A node setting; the payload is passed through untouched (null included).</summary>
internal sealed class NodeSettingHandler(IMqttEventSink sink) : IMqttMessageHandler<NodeSettingRoute>
{
    public Task HandleAsync(NodeSettingRoute route, string? payload) =>
        AsyncEvent.InvokeAllAsync(sink.NodeSettingReceived, new NodeSettingReceivedEventArgs
        {
            NodeId = route.NodeId,
            Setting = route.Setting,
            Payload = payload
        }, nameof(IMqttCoordinator.NodeSettingReceivedAsync));
}

/// <summary>A BLE device report from a node. Empty payloads, and messages nobody is subscribed to, are ignored unparsed.</summary>
internal sealed class DeviceMessageHandler(IMqttEventSink sink) : IMqttMessageHandler<DeviceMessageRoute>
{
    public async Task HandleAsync(DeviceMessageRoute route, string? payload)
    {
        var handlers = sink.DeviceMessageReceived;
        if (handlers == null || string.IsNullOrEmpty(payload)) return;

        var message = MqttPayload.Deserialize<DeviceMessage>(route, payload, "DeviceMessage", "Device message was null after deserialization", "Failed to parse device message");
        await AsyncEvent.InvokeAllAsync(handlers, new DeviceMessageEventArgs { DeviceId = route.DeviceId, NodeId = route.NodeId, Payload = message }, nameof(IMqttCoordinator.DeviceMessageReceivedAsync));
    }
}

/// <summary>
/// Persisted device settings. Always parsed (an empty or invalid payload is reported as malformed even with no
/// subscribers, as before); <see cref="DeviceSettings.OriginalId"/> is set from the topic.
/// </summary>
internal sealed class DeviceSettingsHandler(IMqttEventSink sink) : IMqttMessageHandler<DeviceSettingsRoute>
{
    public Task HandleAsync(DeviceSettingsRoute route, string? payload)
    {
        var settings = MqttPayload.Deserialize<DeviceSettings>(route, payload, "DeviceConfig", "Device settings were null after deserialization", "Failed to parse device settings");
        settings.OriginalId = route.DeviceId;
        return AsyncEvent.InvokeAllAsync(sink.DeviceConfigReceived, new DeviceSettingsEventArgs { DeviceId = route.DeviceId, Payload = settings }, nameof(IMqttCoordinator.DeviceConfigReceivedAsync));
    }
}

/// <summary>Companion-published device attributes (a JSON object). Empty payloads are ignored.</summary>
internal sealed class DeviceAttributesHandler(IMqttEventSink sink) : IMqttMessageHandler<DeviceAttributesRoute>
{
    public async Task HandleAsync(DeviceAttributesRoute route, string? payload)
    {
        var handlers = sink.DeviceAttributesReceived;
        if (handlers == null || string.IsNullOrEmpty(payload)) return;

        var attributes = MqttPayload.Deserialize<Dictionary<string, object>>(route, payload, "DeviceAttributes", "Device attributes were null after deserialization", "Failed to parse device attributes");
        await AsyncEvent.InvokeAllAsync(handlers, new DeviceAttributesEventArgs { DeviceId = route.DeviceId, Attributes = attributes }, nameof(IMqttCoordinator.DeviceAttributesReceivedAsync));
    }
}

/// <summary>
/// Home Assistant <c>device_tracker</c> discovery. An empty payload is a cleared retained config (DeviceId and
/// AutoDiscover null); a config not published by a companion is reported as malformed.
/// </summary>
/// <param name="discoveryLogger">Parse failures and the parsed <see cref="AutoDiscovery"/> log under this, the AutoDiscovery category.</param>
internal sealed class DiscoveryHandler(IMqttEventSink sink, Func<string> discoveryTopic, ILogger? logger, ILogger<AutoDiscovery>? discoveryLogger = null) : IMqttMessageHandler<DiscoveryRoute>
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public Task HandleAsync(DiscoveryRoute route, string? payload)
    {
        var topic = route.Topic;
        try
        {
            _logger.LogTrace("Received discovery message on topic: {Topic}", topic);

            if (string.IsNullOrWhiteSpace(payload))
            {
                // A retained discovery message is cleared by publishing an empty payload (delivered as "" or null)
                sink.RaisePreviousDeviceDiscovered(new PreviousDeviceDiscoveredEventArgs
                {
                    Topic = topic,
                    DiscoveryId = route.DiscoveryId,
                    DeviceId = null,
                    AutoDiscover = null
                });
                return Task.CompletedTask;
            }

            if (!AutoDiscovery.TryDeserialize(topic, payload, out var msg, discoveryTopic(), discoveryLogger) || msg == null)
                throw new MqttMessageProcessingException("Failed to deserialize discovery message", topic, payload, "Discovery");

            sink.RaisePreviousDeviceDiscovered(new PreviousDeviceDiscoveredEventArgs
            {
                Topic = topic,
                DiscoveryId = msg.DiscoveryId,
                DeviceId = msg.Message?.StateTopic?.Split('/').Last(),
                AutoDiscover = msg
            });
            return Task.CompletedTask;
        }
        catch (Exception ex) when (ex is not MqttMessageProcessingException)
        {
            throw new MqttMessageProcessingException("Error processing discovery message", topic, payload, "Discovery", ex);
        }
    }
}

/// <summary>Holds one handler per message family and sends a matched route to the right one.</summary>
internal sealed class MqttMessageHandlers(IMqttEventSink sink, Func<string> discoveryTopic, ILogger? logger, ILogger<AutoDiscovery>? discoveryLogger = null)
{
    private readonly NodeTelemetryHandler _telemetry = new(sink);
    private readonly NodeStatusHandler _status = new(sink);
    private readonly NodeUpdateErrorHandler _updateError = new(sink);
    private readonly NodeSettingHandler _nodeSetting = new(sink);
    private readonly DeviceMessageHandler _deviceMessage = new(sink);
    private readonly DeviceSettingsHandler _deviceSettings = new(sink);
    private readonly DeviceAttributesHandler _deviceAttributes = new(sink);

    private readonly DiscoveryHandler _discovery = new(sink, discoveryTopic, logger, discoveryLogger);

    public Task DispatchAsync(MqttRoute route, string? payload) => route switch
    {
        NodeTelemetryRoute r => _telemetry.HandleAsync(r, payload),
        NodeStatusRoute r => _status.HandleAsync(r, payload),
        NodeUpdateErrorRoute r => _updateError.HandleAsync(r, payload),
        NodeSettingRoute r => _nodeSetting.HandleAsync(r, payload),
        DeviceMessageRoute r => _deviceMessage.HandleAsync(r, payload),
        DeviceSettingsRoute r => _deviceSettings.HandleAsync(r, payload),
        DeviceAttributesRoute r => _deviceAttributes.HandleAsync(r, payload),
        DiscoveryRoute r => _discovery.HandleAsync(r, payload),
        _ => throw new ArgumentOutOfRangeException(nameof(route), route, "No handler for route")
    };
}
