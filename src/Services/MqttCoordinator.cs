using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ESPresense.Events;
using ESPresense.Models;
using ESPresense.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Diagnostics.Logger;
using Newtonsoft.Json;

namespace ESPresense.Services;

public class MqttMessageProcessingException : Exception
{
    public string Topic { get; }
    public string? Payload { get; }
    public string MessageType { get; }

    public MqttMessageProcessingException(string message, string topic, string? payload, string messageType, Exception? innerException = null)
        : base(message, innerException)
    {
        Topic = topic;
        Payload = payload;
        MessageType = messageType;
    }
}

/// <summary>
/// The <see cref="IMqttCoordinator"/> facade. The work is split behind it:
/// <see cref="MqttConnection"/> owns the client and its reconnect state machine, <see cref="MqttTopicRouter"/>
/// maps inbound topics to routes, and <see cref="MqttMessageHandlers"/> parses each message family and raises
/// the events declared here. Topic strings live in <see cref="MqttTopics"/>.
/// </summary>
public class MqttCoordinator : IMqttCoordinator, IMqttEventSink, IDisposable
{
    private readonly ILogger<MqttCoordinator> _logger;
    private readonly IMqttConnection _connection;
    private readonly MqttMessageHandlers _handlers;
    private volatile MqttTopicRouter _router = new("homeassistant");

    public MqttCoordinator(
        ConfigLoader cfg,
        ILogger<MqttCoordinator> logger,
        IMqttNetLogger mqttNetLogger,
        SupervisorConfigLoader supervisorConfigLoader)
    {
        _logger = logger ?? NullLogger<MqttCoordinator>.Instance;
        var connection = new MqttConnection(
            () => GetEffectiveConfig(cfg, supervisorConfigLoader),
            () => new MqttClientFactory(mqttNetLogger).CreateMqttClient(),
            _logger);
        cfg.ConfigChanged += (_, c) => connection.OnConfigChanged(c.Mqtt);

        _connection = connection;
        _connection.MessageReceived += OnMqttMessageReceived;
        _handlers = new MqttMessageHandlers(this, () => DiscoveryTopic, _logger);
    }

    /// <summary>For tests: a coordinator over a given connection.</summary>
    internal MqttCoordinator(IMqttConnection connection, ILogger<MqttCoordinator>? logger = null)
    {
        _logger = logger ?? NullLogger<MqttCoordinator>.Instance;
        _connection = connection;
        _connection.MessageReceived += OnMqttMessageReceived;
        _handlers = new MqttMessageHandlers(this, () => DiscoveryTopic, _logger);
    }

    public string DiscoveryTopic => _connection.DiscoveryTopic;

    /// <summary>
    /// The user's mqtt section if it names a host; otherwise the Supervisor broker with the user's
    /// client-side settings (client_id, read_only, discovery_topic) applied, so read_only still holds
    /// on the add-on where the host is usually left empty.
    /// </summary>
    private static async Task<(ConfigMqtt? Effective, ConfigMqtt User)> GetEffectiveConfig(ConfigLoader cfg, SupervisorConfigLoader supervisorConfigLoader)
    {
        var c = await cfg.ConfigAsync();
        var user = c.Mqtt ??= new ConfigMqtt();
        if (!string.IsNullOrEmpty(user.Host)) return (user, user);

        var supervisor = await supervisorConfigLoader.GetSupervisorConfig();
        if (supervisor == null) return (null, user);

        var config = supervisor.Clone(); // the loader caches its instance
        config.ClientId = user.ClientId;
        config.ReadOnly = user.ReadOnly;
        config.DiscoveryTopic = user.DiscoveryTopic;
        return (config, user);
    }

    public Task WaitForConnectionAsync(CancellationToken cancellationToken = default) =>
        _connection.WaitForConnectionAsync(cancellationToken);

    /// <summary>
    /// Called by the DI container on host shutdown. Stops any pending reconnect back-off so shutdown is
    /// not held up. The client is intentionally not disconnected cleanly: letting the socket drop makes
    /// the broker publish the "offline" will message on espresense/companion/status.
    /// </summary>
    public void Dispose()
    {
        (_connection as IDisposable)?.Dispose();
        GC.SuppressFinalize(this);
    }

    public event Func<DeviceSettingsEventArgs, Task>? DeviceConfigReceivedAsync;

    public event Func<DeviceMessageEventArgs, Task>? DeviceMessageReceivedAsync;
    public event Func<MqttApplicationMessageReceivedEventArgs, Task>? MqttMessageReceivedAsync;
    public event Func<NodeSettingReceivedEventArgs, Task>? NodeSettingReceivedAsync;
    public event Func<NodeTelemetryReceivedEventArgs, Task>? NodeTelemetryReceivedAsync;
    public event Func<NodeTelemetryRemovedEventArgs, Task>? NodeTelemetryRemovedAsync;
    public event Func<NodeStatusReceivedEventArgs, Task>? NodeStatusReceivedAsync;
    public event Func<NodeStatusRemovedEventArgs, Task>? NodeStatusRemovedAsync;
    public event Func<NodeUpdateErrorEventArgs, Task>? NodeUpdateErrorAsync;
    public event EventHandler? MqttMessageMalformed;
    public event EventHandler<PreviousDeviceDiscoveredEventArgs>? PreviousDeviceDiscovered;
    public event Func<DeviceAttributesEventArgs, Task>? DeviceAttributesReceivedAsync;

    // The handlers raise the events above through this view, so the events stay declared on the facade.
    Func<DeviceSettingsEventArgs, Task>? IMqttEventSink.DeviceConfigReceived => DeviceConfigReceivedAsync;
    Func<DeviceMessageEventArgs, Task>? IMqttEventSink.DeviceMessageReceived => DeviceMessageReceivedAsync;
    Func<MqttApplicationMessageReceivedEventArgs, Task>? IMqttEventSink.MqttMessageReceived => MqttMessageReceivedAsync;
    Func<NodeSettingReceivedEventArgs, Task>? IMqttEventSink.NodeSettingReceived => NodeSettingReceivedAsync;
    Func<NodeTelemetryReceivedEventArgs, Task>? IMqttEventSink.NodeTelemetryReceived => NodeTelemetryReceivedAsync;
    Func<NodeTelemetryRemovedEventArgs, Task>? IMqttEventSink.NodeTelemetryRemoved => NodeTelemetryRemovedAsync;
    Func<NodeStatusReceivedEventArgs, Task>? IMqttEventSink.NodeStatusReceived => NodeStatusReceivedAsync;
    Func<NodeStatusRemovedEventArgs, Task>? IMqttEventSink.NodeStatusRemoved => NodeStatusRemovedAsync;
    Func<NodeUpdateErrorEventArgs, Task>? IMqttEventSink.NodeUpdateError => NodeUpdateErrorAsync;
    Func<DeviceAttributesEventArgs, Task>? IMqttEventSink.DeviceAttributesReceived => DeviceAttributesReceivedAsync;
    void IMqttEventSink.RaisePreviousDeviceDiscovered(PreviousDeviceDiscoveredEventArgs args) => PreviousDeviceDiscovered?.Invoke(this, args);

    /// <summary>Awaits every subscriber of an async multicast event; see <see cref="AsyncEvent.InvokeAllAsync{T}"/>.</summary>
    internal Task InvokeAllAsync<T>(Func<T, Task>? handlers, T args, string eventName) =>
        AsyncEvent.InvokeAllAsync(handlers, args, eventName);

    /// <summary>The router for the current discovery prefix; rebuilt when a reconnect changes the prefix.</summary>
    private MqttTopicRouter Router
    {
        get
        {
            var router = _router;
            var discoveryTopic = DiscoveryTopic;
            if (router.DiscoveryTopic == discoveryTopic) return router;
            return _router = new MqttTopicRouter(discoveryTopic);
        }
    }

    /// <summary>Route, dispatch, catch: a bad message is logged and reported, never thrown into the receive loop.</summary>
    internal async Task OnMqttMessageReceived(MqttApplicationMessageReceivedEventArgs arg)
    {
        var topic = arg.ApplicationMessage.Topic;
        var payload = arg.ApplicationMessage.ConvertPayloadToString();

        try
        {
            var route = Router.Match(topic);
            if (route == null)
                await InvokeAllAsync(MqttMessageReceivedAsync, arg, nameof(MqttMessageReceivedAsync));
            else
                await _handlers.DispatchAsync(route, payload);
        }
        catch (JsonSerializationException ex)
        {
            _logger.LogError(ex, "JSON deserialization error for topic {Topic}. Payload: {Payload}", topic, payload);
            MqttMessageMalformed?.Invoke(this, EventArgs.Empty);
        }
        catch (MqttMessageProcessingException ex)
        {
            _logger.LogError(ex, "Error processing {MessageType} message for topic {Topic}. Payload: {Payload}", ex.MessageType, ex.Topic, ex.Payload);
            MqttMessageMalformed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error processing message for topic {Topic}. Payload: {Payload}", topic, payload);
            MqttMessageMalformed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Handles a Home Assistant discovery message (also used directly by tests).</summary>
    internal Task ProcessDiscoveryMessage(string topic, string? payload) => _handlers.Discovery.HandleAsync(topic, payload);

    /// <summary>Why the coordinator is (or is not) in read-only mode.</summary>
    internal enum ReadOnlyReason
    {
        None,
        /// <summary><c>mqtt.read_only: true</c> in config.</summary>
        ConfigFlag,
        /// <summary>Deprecated heuristic: the client id contains "read".</summary>
        ClientIdHeuristic
    }

    /// <summary>
    /// Decides read-only mode for a config: the explicit <see cref="ConfigMqtt.ReadOnly"/> flag wins;
    /// otherwise the deprecated client-id heuristic is applied for backward compatibility (and
    /// <see cref="MqttConnection"/> logs a deprecation warning whenever it decides the outcome).
    /// </summary>
    internal static ReadOnlyReason GetReadOnlyReason(ConfigMqtt config)
    {
        if (config.ReadOnly) return ReadOnlyReason.ConfigFlag;
        if (IsReadOnlyClient(config.ClientId)) return ReadOnlyReason.ClientIdHeuristic;
        return ReadOnlyReason.None;
    }

    /// <summary>
    /// Deprecated heuristic kept for backward compatibility: a client id containing "read"
    /// (case-insensitive) puts the coordinator in read-only mode. Use <see cref="ConfigMqtt.ReadOnly"/>.
    /// </summary>
    internal static bool IsReadOnlyClient(string? clientId)
    {
        return clientId?.Contains("read", StringComparison.OrdinalIgnoreCase) ?? false;
    }

    /// <summary>
    /// Publishes an MQTT message and awaits the publish (there is no outbound queue: the call
    /// completes when the underlying client has sent the message, or throws). Connects the client
    /// on demand and reconnects it if it has dropped. In read-only mode the message is not sent;
    /// the intent is logged at Information level instead.
    /// </summary>
    /// <param name="topic">MQTT topic to publish to.</param>
    /// <param name="payload">Message payload; may be null to clear retained messages for the topic.</param>
    /// <param name="retain">If true, the broker will retain the message.</param>
    /// <returns>A task that completes when the message has been published or the intent has been logged.</returns>
    /// <exception cref="Exception">Propagates exceptions thrown by the underlying MQTT client when connecting or publishing fails.</exception>
    public virtual Task EnqueueAsync(string topic, string? payload, bool retain = false) =>
        _connection.PublishAsync(topic, payload, retain);

    /// <summary>
    /// Publishes an MQTT message like <see cref="EnqueueAsync"/> but never throws (except for cancellation).
    /// Logs errors and returns success/failure status instead of propagating exceptions.
    /// Use this for best-effort publishes (telemetry, status updates) that shouldn't crash background services.
    /// </summary>
    /// <param name="topic">MQTT topic to publish to.</param>
    /// <param name="payload">Message payload; may be null to clear retained messages for the topic.</param>
    /// <param name="retain">If true, the broker will retain the message.</param>
    /// <returns>True if the message was published (or logged in read-only mode), false if an error occurred.</returns>
    public virtual async Task<bool> TryEnqueueAsync(string topic, string? payload, bool retain = false)
    {
        try
        {
            await EnqueueAsync(topic, payload, retain).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Already logged by EnqueueAsync, just return false
            // Common causes: DNS resolution failure, network unreachable, broker down
            return false;
        }
    }

    /// <summary>
    /// Clears retained MQTT messages that match a topic filter by subscribing to the filter, collecting the exact topics seen, and republishing each topic with a null payload and retain=true.
    /// </summary>
    /// <remarks>
    /// The method temporarily subscribes to <paramref name="topicFilter"/>, waits briefly to receive retained messages (1 second, cancellable), unsubscribes, and then republishes a null retained message for each distinct topic observed to clear the retained state on the broker.
    /// </remarks>
    /// <param name="topicFilter">An MQTT topic filter (may include wildcards) used to discover retained topics to clear.</param>
    /// <param name="cancellationToken">A token to cancel the waiting period for incoming retained messages.</param>
    /// <returns>A task that completes when all discovered retained topics have been cleared.</returns>
    public async Task ClearRetainedAsync(string topicFilter, CancellationToken cancellationToken = default)
    {
        var client = await _connection.GetClientAsync(cancellationToken);
        var topics = new HashSet<string>();

        Task handler(MqttApplicationMessageReceivedEventArgs arg)
        {
            topics.Add(arg.ApplicationMessage.Topic);
            return Task.CompletedTask;
        }

        try
        {
            client.ApplicationMessageReceivedAsync += handler;
            await client.SubscribeAsync(topicFilter);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            await client.UnsubscribeAsync(topicFilter);

            foreach (var topic in topics)
                await EnqueueAsync(topic, null, true);

            var sanitizedPattern = topicFilter.Replace(Environment.NewLine, "").Replace("\n", "").Replace("\r", "");
            _logger.LogDebug("Cleared {Count} retained messages for pattern {Pattern}", topics.Count, sanitizedPattern);
        }
        finally
        {
            client.ApplicationMessageReceivedAsync -= handler;
        }
    }
}
