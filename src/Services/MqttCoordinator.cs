using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ESPresense.Events;
using ESPresense.Extensions;
using ESPresense.Models;
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

public class MqttCoordinator : IMqttCoordinator, IDisposable
{
    private readonly ConfigLoader _cfg;
    private readonly ILogger<MqttCoordinator> _logger;
    private readonly IMqttNetLogger _mqttNetLogger;
    private readonly SupervisorConfigLoader _supervisorConfigLoader;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly object _reconnectSync = new();
    // Cancelled on Dispose (host shutdown) so a reconnect back-off does not outlive the host.
    private readonly CancellationTokenSource _shutdownCts = new();
    // Written from the config-change callback / GetClient and read from publishers and the reconnect loop
    // without a common lock, so both must be volatile to guarantee visibility across threads.
    private volatile IMqttClient? _mqttClient;
    private Task<IMqttClient>? _initTask;
    private Task? _reconnectTask;
    private ConfigMqtt? _lastConfig;
    private volatile bool _reconnectRequired;
    private string _discoveryTopic = "homeassistant";
    private volatile TaskCompletionSource _connectionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string DiscoveryTopic => _discoveryTopic;

    public MqttCoordinator(
        ConfigLoader cfg,
        ILogger<MqttCoordinator> logger,
        IMqttNetLogger mqttNetLogger,
        SupervisorConfigLoader supervisorConfigLoader)
    {
        _cfg = cfg;
        _logger = logger;
        _mqttNetLogger = mqttNetLogger;
        _supervisorConfigLoader = supervisorConfigLoader;
        _cfg.ConfigChanged += (s, c) =>
        {
            var configChanged = ConfigChanged(c.Mqtt);
            if (configChanged) _logger.LogInformation("MQTT configuration changed");
            _reconnectRequired |= configChanged;
        };
    }

    private bool ConfigChanged(ConfigMqtt? config)
    {
        if (config == null) return _lastConfig != null;
        return _lastConfig != null &&
             (!string.Equals(_lastConfig.Host, config.Host, StringComparison.OrdinalIgnoreCase) ||
              _lastConfig.Port != config.Port ||
              !string.Equals(_lastConfig.Username, config.Username) ||
              !string.Equals(_lastConfig.Password, config.Password) ||
              _lastConfig.Ssl != config.Ssl ||
              !string.Equals(_lastConfig.ClientId, config.ClientId) ||
              _lastConfig.ReadOnly != config.ReadOnly ||
              !string.Equals(_lastConfig.DiscoveryTopic, config.DiscoveryTopic, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ConfigMqtt?> GetUserConfig()
    {
        var c = await _cfg.ConfigAsync();

        c.Mqtt ??= new ConfigMqtt();

        return string.IsNullOrEmpty(c.Mqtt.Host) ? null : c.Mqtt;
    }

    private async Task<IMqttClient> GetClient()
    {
        // Snapshot the volatile field once per check so a concurrent reset cannot turn the
        // "not null" test into a null return.
        var client = _mqttClient;
        if (client != null && !_reconnectRequired)
        {
            return client;
        }

        await _initLock.WaitAsync();
        try
        {
            client = _mqttClient;
            if (client != null && !_reconnectRequired)
            {
                return client;
            }

            if (_reconnectRequired && client != null)
            {
                try
                {
                    await client.DisconnectAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disconnecting MQTT client during reconnection");
                }

                // Publishers that already hold this reference will get ObjectDisposedException
                // and retry once via GetClient() (see EnqueueAsync).
                client.Dispose();
                _mqttClient = null;
                _initTask = null;

                lock (_reconnectSync)
                {
                    _reconnectTask = null;
                }
            }

            if (_initTask != null)
            {
                if (_initTask.IsFaulted)
                    _initTask = null;
                else
                    return await _initTask.ConfigureAwait(false);
            }

            _initTask = InitializeClientAsync();
            try
            {
                return await _initTask.ConfigureAwait(false);
            }
            catch
            {
                _initTask = null;
                throw;
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<IMqttClient> InitializeClientAsync()
    {
        var config = await GetUserConfig() ?? await _supervisorConfigLoader.GetSupervisorConfig();
        if (config == null)
        {
            throw new InvalidOperationException("No MQTT server setup, fix your config.yaml");
        }

        var mqttFactory = new MqttClientFactory(_mqttNetLogger);
        var mqttClient = mqttFactory.CreateMqttClient();

        mqttClient.ApplicationMessageReceivedAsync += OnMqttMessageReceived;

        var readOnlyReason = GetReadOnlyReason(config);

        mqttClient.ConnectedAsync += async _ =>
        {
            _logger.LogInformation("MQTT connected!");

            switch (readOnlyReason)
            {
                case ReadOnlyReason.ConfigFlag:
                    _logger.LogInformation("MQTT read-only mode is active (mqtt.read_only: true); nothing will be published");
                    break;
                case ReadOnlyReason.ClientIdHeuristic:
                    _logger.LogInformation(
                        "MQTT read-only mode is active because client_id '{ClientId}' contains \"read\" (legacy heuristic); set mqtt.read_only: true explicitly, or choose a different client_id to publish",
                        config.ClientId);
                    break;
            }

            try
            {
                if (readOnlyReason == ReadOnlyReason.None)
                {
                    await mqttClient.PublishStringAsync("espresense/companion/status", "online").ConfigureAwait(false);
                }

                await mqttClient.SubscribeAsync("espresense/devices/+/+").ConfigureAwait(false);
                await mqttClient.SubscribeAsync("espresense/settings/+/config").ConfigureAwait(false);
                await mqttClient.SubscribeAsync("espresense/rooms/+/+").ConfigureAwait(false);
                await mqttClient.SubscribeAsync("espresense/rooms/*/+/set").ConfigureAwait(false);
                await mqttClient.SubscribeAsync($"{config.DiscoveryTopic}/device_tracker/+/config").ConfigureAwait(false);
                await mqttClient.SubscribeAsync("espresense/companion/+/attributes").ConfigureAwait(false);
                await mqttClient.SubscribeAsync("espresense/companion/lease/+").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The session is not usable without its subscriptions, so the connection TCS is
                // deliberately left pending (WaitForConnectionAsync keeps waiting for a good session).
                //
                // Rethrowing is the safe way to drop the session with MQTTnet 5: ConnectedAsync is awaited
                // inside MqttClient.ConnectAsync's try block, so the exception makes ConnectAsync tear the
                // connection down itself (DisconnectInternal -> our DisconnectedAsync handler, which starts
                // the reconnect loop) and then surface the failure to whoever called ConnectAsync:
                // InitializeClientAsync disposes the client so the next GetClient() builds a fresh one
                // (no reconnect loop is started for it: _mqttClient is only published after success),
                // ReconnectLoopAsync logs and retries after its back-off, EnsureClientConnectedAsync
                // propagates to the publisher. Calling DisconnectAsync from inside this handler would
                // instead make ConnectAsync report success for a session that is already gone.
                _logger.LogError(ex, "MQTT post-connect setup failed (status publish / subscriptions); dropping the connection so it is retried");
                throw;
            }

            _connectionTcs.TrySetResult();
        };

        mqttClient.DisconnectedAsync += args =>
        {
            if (_connectionTcs.Task.IsCompleted)
                _connectionTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // Enhanced logging with reason details for troubleshooting connection issues
            if (args.Exception != null)
            {
                _logger.LogWarning(args.Exception,
                    "MQTT disconnected due to error: {ExceptionType}. Broker: {Broker}",
                    args.Exception.GetType().Name,
                    config.Host);
            }
            else if (args.Reason != MqttClientDisconnectReason.NormalDisconnection)
            {
                _logger.LogWarning("MQTT disconnected with reason: {Reason} ({ReasonCode}). Client was connected: {WasConnected}. Broker: {Broker}",
                    args.Reason.ToString(),
                    (int)args.Reason,
                    args.ClientWasConnected,
                    config.Host);
            }
            else
            {
                _logger.LogInformation("MQTT disconnected. Client was connected: {WasConnected}. Broker: {Broker}",
                    args.ClientWasConnected,
                    config.Host);
            }

            if (!_reconnectRequired && args.ClientWasConnected)
            {
                StartReconnectLoop(mqttClient);
            }

            return Task.CompletedTask;
        };

        mqttClient.ConnectingAsync += _ =>
        {
            _logger.LogDebug("MQTT connecting...");
            return Task.CompletedTask;
        };

        var mqttClientOptions = new MqttClientOptionsBuilder()
            .WithConfig(config)
            .WithClientId(config.ClientId)
            .WithWillTopic("espresense/companion/status")
            .WithWillRetain()
            .WithWillPayload("offline")
            .WithCleanSession()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .Build();

        _logger.LogInformation("Connecting to MQTT at {Host}{Port} as {User}",
            config.Host,
            config.Port.HasValue ? ":" + config.Port : "",
            string.IsNullOrEmpty(config.Username) ? "<anonymous>" : config.Username);

        try
        {
            await mqttClient.ConnectAsync(mqttClientOptions).ConfigureAwait(false);
        }
        catch (MQTTnet.Exceptions.MqttClientNotConnectedException ex)
        {
            _logger.LogError(ex, "MQTT client not connected after connect attempt. Broker may be rejecting the connection (check protocol version).");
            mqttClient.Dispose();
            throw;
        }
        catch (MQTTnet.Exceptions.MqttCommunicationException ex)
        {
            _logger.LogError(ex, "MQTT connection failed: {Message}. Check broker address, port, and TLS settings.", ex.Message);
            mqttClient.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MQTT connection failed unexpectedly: {Type} - {Message}", ex.GetType().Name, ex.Message);
            mqttClient.Dispose();
            throw;
        }

        // Publish the client only once the initial connect (including post-connect setup) succeeded.
        // A DisconnectedAsync raised while ConnectAsync is still unwinding a failed connect therefore
        // does not start a reconnect loop on a client that is about to be disposed above.
        _mqttClient = mqttClient;
        _lastConfig = config.Clone();
        _discoveryTopic = config.DiscoveryTopic;
        _reconnectRequired = false;

        // Window: a DisconnectedAsync that fired after ConnectedAsync succeeded but before the
        // assignment above failed StartReconnectLoop's ReferenceEquals(_mqttClient, client) guard, so no
        // reconnect loop was started for it. In normal mode the next publish would self-heal through
        // EnsureClientConnectedAsync, but in read-only mode EnqueueAsync never gets that far, so start
        // the loop explicitly now (StartReconnectLoop is idempotent under _reconnectSync).
        if (!mqttClient.IsConnected)
        {
            _logger.LogWarning("MQTT connection dropped immediately after connecting; starting reconnect loop");
            StartReconnectLoop(mqttClient);
        }

        return mqttClient;
    }

    private void StartReconnectLoop(IMqttClient client)
    {
        lock (_reconnectSync)
        {
            if (!ReferenceEquals(_mqttClient, client))
            {
                return;
            }

            if (_reconnectTask != null && !_reconnectTask.IsCompleted)
            {
                return;
            }

            var shutdownToken = _shutdownCts.Token;
            var reconnectTask = Task.Run(() => ReconnectLoopAsync(client, shutdownToken));
            _reconnectTask = reconnectTask;

            reconnectTask.ContinueWith(_ =>
            {
                lock (_reconnectSync)
                {
                    if (ReferenceEquals(_reconnectTask, reconnectTask))
                    {
                        _reconnectTask = null;
                    }
                }
            }, TaskScheduler.Default);
        }
    }

    private async Task ReconnectLoopAsync(IMqttClient client, CancellationToken shutdownToken)
    {
        while (!_reconnectRequired && !shutdownToken.IsCancellationRequested)
        {
            if (!ReferenceEquals(_mqttClient, client))
            {
                return;
            }

            if (client.IsConnected)
            {
                return;
            }

            try
            {
                await _connectionLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (client.IsConnected)
                    {
                        return;
                    }

                    if (client.Options == null)
                    {
                        _logger.LogWarning("MQTT reconnect skipped because client options are not set.");
                        return;
                    }

                    _logger.LogInformation("Attempting MQTT reconnect...");
                    await client.ConnectAsync(client.Options).ConfigureAwait(false);
                    return;
                }
                finally
                {
                    _connectionLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MQTT reconnect attempt failed, retrying in 30 seconds");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), shutdownToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Host is shutting down; stop retrying.
                return;
            }
        }
    }

    private async Task EnsureClientConnectedAsync(IMqttClient client)
    {
        if (client.IsConnected || _reconnectRequired)
        {
            return;
        }

        await _connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (client.IsConnected || _reconnectRequired)
            {
                return;
            }

            if (client.Options == null)
            {
                _logger.LogWarning("MQTT reconnect skipped because client options are not set.");
                return;
            }

            _logger.LogDebug("MQTT client was disconnected when publishing; attempting reconnect.");
            await client.ConnectAsync(client.Options).ConfigureAwait(false);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task WaitForConnectionAsync(CancellationToken cancellationToken = default)
    {
        var tcs = _connectionTcs;
        await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Called by the DI container on host shutdown. Stops any pending reconnect back-off so shutdown is
    /// not held up. The client is intentionally not disconnected cleanly: letting the socket drop makes
    /// the broker publish the "offline" will message on espresense/companion/status.
    /// </summary>
    public void Dispose()
    {
        // The CTS is not disposed on purpose: a reconnect loop may still read the token afterwards.
        _shutdownCts.Cancel();
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
    public event EventHandler? MqttMessageMalformed;
    public event EventHandler<PreviousDeviceDiscoveredEventArgs>? PreviousDeviceDiscovered;
    public event Func<DeviceAttributesEventArgs, Task>? DeviceAttributesReceivedAsync;

    /// <summary>
    /// Awaits every subscriber of an async multicast event.
    /// </summary>
    /// <remarks>
    /// <c>await SomeEvent(args)</c> only awaits the <em>last</em> subscriber's task; earlier
    /// handlers run but their tasks (and exceptions) are unobserved. This helper awaits each
    /// handler so all subscribers complete and any failure surfaces. Handlers are invoked on
    /// the captured invocation list, so a subscriber that unsubscribes mid-dispatch does not
    /// skew the iteration, and exceptions from one handler do not stop the others.
    /// </remarks>
    internal Task InvokeAllAsync<T>(Func<T, Task>? handlers, T args, string eventName)
    {
        if (handlers == null)
            return Task.CompletedTask;

        var invocations = handlers.GetInvocationList();
        return AwaitAll(invocations, args, eventName);

        async Task AwaitAll(Delegate[] list, T arg, string name)
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

    internal async Task OnMqttMessageReceived(MqttApplicationMessageReceivedEventArgs arg)
    {
        var parts = arg.ApplicationMessage.Topic.Split('/');
        var payload = arg.ApplicationMessage.ConvertPayloadToString();

        try
        {
            // Check for Home Assistant discovery messages with configurable topic
            if (parts.Length == 4 && parts[0] == _discoveryTopic && parts[1] == "device_tracker" && parts[3] == "config")
            {
                await ProcessDiscoveryMessage(arg.ApplicationMessage.Topic, payload);
                return;
            }

            switch (parts)
            {
                case ["espresense", "rooms", _, "telemetry"]:
                    await ProcessTelemetryMessage(parts[2], payload);
                    break;
                case ["espresense", "rooms", _, "status"]:
                    await ProcessStatusMessage(parts[2], payload);
                    break;
                case ["espresense", "rooms", "*", _, "set"]:
                    await ProcessNodeSettingMessage(parts[2], parts[3], payload);
                    break;
                case ["espresense", "rooms", _, _]:
                    await ProcessNodeSettingMessage(parts[2], parts[3], payload);
                    break;
                case ["espresense", "devices", _, _]:
                    await ProcessDeviceMessage(parts[2], parts[3], payload);
                    break;
                case ["espresense", "settings", _, "config"]:
                    await ProcessDeviceConfigMessage(parts[2], payload);
                    break;
                case ["espresense", "companion", _, "attributes"]:
                    await ProcessDeviceAttributesMessage(parts[2], payload);
                    break;
                default:
                    if (MqttMessageReceivedAsync != null)
                        await InvokeAllAsync(MqttMessageReceivedAsync, arg, nameof(MqttMessageReceivedAsync));
                    break;
            }
        }
        catch (JsonSerializationException ex)
        {
            _logger.LogError(ex, "JSON deserialization error for topic {Topic}. Payload: {Payload}", arg.ApplicationMessage.Topic, payload);
            MqttMessageMalformed?.Invoke(this, EventArgs.Empty);
        }
        catch (MqttMessageProcessingException ex)
        {
            _logger.LogError(ex, "Error processing {MessageType} message for topic {Topic}. Payload: {Payload}", ex.MessageType, ex.Topic, ex.Payload);
            MqttMessageMalformed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error processing message for topic {Topic}. Payload: {Payload}", arg.ApplicationMessage.Topic, payload);
            MqttMessageMalformed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Why the coordinator is (or is not) in read-only mode.</summary>
    internal enum ReadOnlyReason
    {
        None,
        /// <summary><c>mqtt.read_only: true</c> in config.</summary>
        ConfigFlag,
        /// <summary>Legacy heuristic: the client id contains "read".</summary>
        ClientIdHeuristic
    }

    private bool ReadOnly
    {
        get
        {
            var config = _lastConfig;
            if (config != null)
                return GetReadOnlyReason(config) != ReadOnlyReason.None;

            // No successful connect yet (e.g. mocked coordinator): fall back to the client options.
            return IsReadOnlyClient(_mqttClient?.Options?.ClientId);
        }
    }

    /// <summary>
    /// Decides read-only mode for a config: the explicit <see cref="ConfigMqtt.ReadOnly"/> flag wins;
    /// otherwise the legacy client-id heuristic is applied for backward compatibility.
    /// </summary>
    internal static ReadOnlyReason GetReadOnlyReason(ConfigMqtt config)
    {
        if (config.ReadOnly) return ReadOnlyReason.ConfigFlag;
        if (IsReadOnlyClient(config.ClientId)) return ReadOnlyReason.ClientIdHeuristic;
        return ReadOnlyReason.None;
    }

    /// <summary>
    /// Legacy heuristic kept for backward compatibility: a client id containing "read"
    /// (case-insensitive) puts the coordinator in read-only mode. Prefer <see cref="ConfigMqtt.ReadOnly"/>.
    /// </summary>
    internal static bool IsReadOnlyClient(string? clientId)
    {
        return clientId?.Contains("read", StringComparison.OrdinalIgnoreCase) ?? false;
    }

    private static string Sanitize(string value) =>
        value.Replace(Environment.NewLine, "").Replace("\n", "").Replace("\r", "");

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
    public virtual async Task EnqueueAsync(string topic, string? payload, bool retain = false)
    {
        // At most one retry: a config reload (GetClient) may dispose the client after we took the
        // reference, which surfaces as ObjectDisposedException; the replacement client is then used.
        for (var attempt = 0; ; attempt++)
        {
            var client = await GetClient().ConfigureAwait(false);

            if (ReadOnly)
            {
                var sanitizedPayload = payload == null ? null : Sanitize(payload);
                _logger.LogInformation("ReadOnly, would have sent to {Topic}: {Payload}", Sanitize(topic), sanitizedPayload);
                return;
            }

            try
            {
                await EnsureClientConnectedAsync(client).ConfigureAwait(false);
                await client.PublishStringAsync(topic, payload, retain: retain).ConfigureAwait(false);
                return;
            }
            catch (ObjectDisposedException ex) when (attempt == 0)
            {
                _logger.LogDebug(ex, "MQTT client was replaced while publishing to {Topic}; retrying once with the new client", Sanitize(topic));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish MQTT message to {Topic}", Sanitize(topic));
                throw;
            }
        }
    }

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
        var client = await GetClient();
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

    /// <summary>
    /// Process a telemetry MQTT message for a given node.
    /// </summary>
    /// <remarks>
    /// If <paramref name="payload"/> is null or empty, raises <see cref="NodeTelemetryRemovedAsync"/> (if subscribed) and returns.
    /// Otherwise deserializes the JSON payload into a <see cref="NodeTelemetry"/> and invokes <see cref="NodeTelemetryReceivedAsync"/> (if subscribed).
    /// </remarks>
    /// <param name="nodeId">The identifier of the node the telemetry came from.</param>
    /// <param name="payload">The telemetry JSON payload; may be null or empty to indicate removal.</param>
    /// <returns>A task that completes when processing is finished.</returns>
    /// <exception cref="MqttMessageProcessingException">
    /// Thrown when deserialization produces null or when JSON parsing fails; includes topic, payload, and message type context.
    /// </exception>
    private async Task ProcessTelemetryMessage(string nodeId, string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            if (NodeTelemetryRemovedAsync != null)
            {
                await InvokeAllAsync(NodeTelemetryRemovedAsync, new NodeTelemetryRemovedEventArgs
                {
                    NodeId = nodeId
                }, nameof(NodeTelemetryRemovedAsync));
            }
            return;
        }

        if (NodeTelemetryReceivedAsync == null)
            return;

        try
        {
            var telemetry = JsonConvert.DeserializeObject<NodeTelemetry>(payload);
            if (telemetry == null)
                throw new MqttMessageProcessingException(
                    "Telemetry data was null after deserialization",
                    $"espresense/rooms/{nodeId}/telemetry",
                    payload,
                    "Telemetry");

            await InvokeAllAsync(NodeTelemetryReceivedAsync, new NodeTelemetryReceivedEventArgs
            {
                NodeId = nodeId,
                Payload = telemetry
            }, nameof(NodeTelemetryReceivedAsync));
        }
        catch (JsonException ex)
        {
            throw new MqttMessageProcessingException(
                "Failed to parse telemetry data",
                $"espresense/rooms/{nodeId}/telemetry",
                payload,
                "Telemetry",
                ex);
        }
    }

    /// <summary>
    /// Handles an incoming node status MQTT message by raising the appropriate event.
    /// </summary>
    /// <remarks>
    /// If <paramref name="payload"/> is null or empty the method treats this as a cleared retained message and invokes
    /// <see cref="NodeStatusRemovedAsync"/> (if any handlers are attached) with the node identifier. Otherwise it interprets
    /// the payload value "online" as an online state and invokes <see cref="NodeStatusReceivedAsync"/> with the resulting state.
    /// </remarks>
    /// <param name="nodeId">The identifier of the node the status message pertains to.</param>
    /// <param name="payload">
    /// The message payload; null or empty means the retained status was cleared (node removed). A non-empty value is
    /// interpreted as the node's status (the string "online" maps to true).
    /// </param>
    /// <returns>A task representing the asynchronous event dispatch operations.</returns>
    private async Task ProcessStatusMessage(string nodeId, string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            // Retained message cleared - node removed
            if (NodeStatusRemovedAsync != null)
            {
                await InvokeAllAsync(NodeStatusRemovedAsync, new NodeStatusRemovedEventArgs
                {
                    NodeId = nodeId
                }, nameof(NodeStatusRemovedAsync));
            }
        }
        else
        {
            // Normal status update
            if (NodeStatusReceivedAsync != null)
            {
                var online = payload == "online";
                await InvokeAllAsync(NodeStatusReceivedAsync, new NodeStatusReceivedEventArgs
                {
                    NodeId = nodeId,
                    Online = online
                }, nameof(NodeStatusReceivedAsync));
            }
        }
    }

    /// <summary>
    /// Processes an incoming device message payload and invokes the DeviceMessageReceivedAsync handler.
    /// </summary>
    /// <remarks>
    /// If <paramref name="payload"/> is null or empty, or there is no subscriber to <see cref="DeviceMessageReceivedAsync"/>, the method returns without action.
    /// On successful JSON deserialization the <see cref="DeviceMessageReceivedAsync"/> event is invoked with a <see cref="DeviceMessageEventArgs"/> containing the device and node IDs and the parsed payload.
    /// </remarks>
    /// <param name="deviceId">MQTT device identifier from the topic (e.g. the {device} segment).</param>
    /// <param name="nodeId">MQTT node identifier from the topic (e.g. the {node} segment).</param>
    /// <param name="payload">JSON payload to deserialize into a <see cref="DeviceMessage"/>; may be null.</param>
    /// <exception cref="MqttMessageProcessingException">
    /// Thrown when JSON deserialization fails or results in a null <see cref="DeviceMessage"/>; the exception includes topic, payload and message type context.
    /// </exception>
    private async Task ProcessDeviceMessage(string deviceId, string nodeId, string? payload)
    {
        if (DeviceMessageReceivedAsync == null || string.IsNullOrEmpty(payload))
            return;

        try
        {
            var deviceMessage = JsonConvert.DeserializeObject<DeviceMessage>(payload ?? "");
            if (deviceMessage == null)
                throw new MqttMessageProcessingException("Device message was null after deserialization", $"espresense/devices/{deviceId}/{nodeId}", payload, "DeviceMessage");

            await InvokeAllAsync(DeviceMessageReceivedAsync, new DeviceMessageEventArgs
            {
                DeviceId = deviceId,
                NodeId = nodeId,
                Payload = deviceMessage
            }, nameof(DeviceMessageReceivedAsync));
        }
        catch (JsonException ex)
        {
            throw new MqttMessageProcessingException("Failed to parse device message", $"espresense/devices/{deviceId}/{nodeId}", payload, "DeviceMessage", ex);
        }
    }


    private async Task ProcessDeviceConfigMessage(string deviceId, string? payload)
    {
        try
        {
            var deviceSettings = JsonConvert.DeserializeObject<DeviceSettings>(payload ?? "");
            if (deviceSettings == null)
                throw new MqttMessageProcessingException("Device settings were null after deserialization", $"espresense/settings/{deviceId}/config", payload, "DeviceConfig");

            deviceSettings.OriginalId = deviceId;
            if (DeviceConfigReceivedAsync != null)
                await InvokeAllAsync(DeviceConfigReceivedAsync, new DeviceSettingsEventArgs
                {
                    DeviceId = deviceId,
                    Payload = deviceSettings
                }, nameof(DeviceConfigReceivedAsync));
        }
        catch (JsonException ex)
        {
            throw new MqttMessageProcessingException(
                "Failed to parse device settings",
                $"espresense/settings/{deviceId}/config",
                payload,
                "DeviceConfig",
                ex);
        }
    }

    private async Task ProcessNodeSettingMessage(string nodeId, string setting, string? payload)
    {
        if (NodeSettingReceivedAsync == null) return;
        await InvokeAllAsync(NodeSettingReceivedAsync, new NodeSettingReceivedEventArgs
        {
            NodeId = nodeId,
            Setting = setting,
            Payload = payload
        }, nameof(NodeSettingReceivedAsync));
    }

    private async Task ProcessDiscoveryMessage(string topic, string? payload)
    {
        try
        {
            _logger.LogTrace($"Received discovery message on topic: {topic}");

            if (payload == null)
            {
                // Null payload indicates deletion of retained message
                PreviousDeviceDiscovered?.Invoke(this, new PreviousDeviceDiscoveredEventArgs { AutoDiscover = null });
                return;
            }

            if (!AutoDiscovery.TryDeserialize(topic, payload, out var msg, _discoveryTopic))
                throw new MqttMessageProcessingException("Failed to deserialize discovery message", topic, payload, "Discovery");

            PreviousDeviceDiscovered?.Invoke(this, new PreviousDeviceDiscoveredEventArgs { AutoDiscover = msg });
        }
        catch (Exception ex) when (ex is not MqttMessageProcessingException)
        {
            throw new MqttMessageProcessingException("Error processing discovery message", topic, payload, "Discovery", ex);
        }
    }

    private async Task ProcessDeviceAttributesMessage(string deviceId, string? payload)
    {
        if (DeviceAttributesReceivedAsync == null) return;

        try
        {
            if (string.IsNullOrEmpty(payload))
                return;

            var attributes = JsonConvert.DeserializeObject<Dictionary<string, object>>(payload);
            if (attributes == null)
                throw new MqttMessageProcessingException(
                    "Device attributes were null after deserialization",
                    $"espresense/companion/{deviceId}/attributes",
                    payload,
                    "DeviceAttributes");

            await InvokeAllAsync(DeviceAttributesReceivedAsync, new DeviceAttributesEventArgs
            {
                DeviceId = deviceId,
                Attributes = attributes
            }, nameof(DeviceAttributesReceivedAsync));
        }
        catch (JsonException ex)
        {
            throw new MqttMessageProcessingException(
                "Failed to parse device attributes",
                $"espresense/companion/{deviceId}/attributes",
                payload,
                "DeviceAttributes", ex);
        }
    }
}
