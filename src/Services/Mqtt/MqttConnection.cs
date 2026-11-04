using ESPresense.Extensions;
using ESPresense.Models;
using ESPresense.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;

namespace ESPresense.Services;

/// <summary>Owns the MQTTnet client: connect, reconnect, config-change reconnect, publish.</summary>
internal interface IMqttConnection
{
    /// <summary>True when nothing may be published (see <see cref="MqttReadOnly.GetReason"/>).</summary>
    bool IsReadOnly { get; }

    /// <summary>The Home Assistant discovery prefix of the current connection (<c>homeassistant</c> until the first connect).</summary>
    string DiscoveryTopic { get; }

    /// <summary>The connected client, connecting (or reconnecting after a config change) first if needed.</summary>
    Task<IMqttClient> GetClientAsync(CancellationToken ct = default);

    /// <summary>Publishes, reconnecting a dropped client first. In read-only mode only logs the intent.</summary>
    Task PublishAsync(string topic, string? payload, bool retain, CancellationToken ct = default);

    /// <summary>Completes once connected with every subscription in place.</summary>
    Task WaitForConnectionAsync(CancellationToken ct = default);

    /// <summary>Every application message received by the current client.</summary>
    event Func<MqttApplicationMessageReceivedEventArgs, Task>? MessageReceived;
}

/// <summary>
/// The reconnect state machine behind <see cref="MqttCoordinator"/>.
/// </summary>
/// <remarks>
/// <para>States: no client → (first <see cref="GetClientAsync"/>) connecting → connected. A drop of a connected
/// client starts one background reconnect loop (retrying every <see cref="ReconnectDelay"/>); a publish during a
/// drop reconnects inline. A relevant config change marks a reconnect as required: the next
/// <see cref="GetClientAsync"/> disconnects and disposes the old client and builds a new one, and the old
/// client's reconnect loop exits.</para>
/// <para>Locks: <c>_initLock</c> serialises building/replacing the client; <c>_connectionLock</c> serialises
/// <c>ConnectAsync</c> on an existing client (reconnect loop vs. publish path); <c>_reconnectSync</c> guards
/// <c>_reconnectTask</c>.</para>
/// </remarks>
internal sealed class MqttConnection : IMqttConnection, IDisposable
{
    /// <summary>Resolves the effective broker config and the user's own mqtt section (see <see cref="MqttCoordinator"/>).</summary>
    internal delegate Task<(ConfigMqtt? Effective, ConfigMqtt User)> ConfigSource();

    private readonly ConfigSource _getConfig;
    private readonly Func<IMqttClient> _createClient;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly object _reconnectSync = new();
    // Cancelled on Dispose (host shutdown) so a reconnect back-off does not outlive the host.
    private readonly CancellationTokenSource _shutdownCts = new();

    // Written under _initLock (GetClientAsync/InitializeClientAsync) and read without a lock by publishers, the
    // DisconnectedAsync callback and the reconnect loop, hence volatile.
    private volatile IMqttClient? _mqttClient;
    private Task<IMqttClient>? _initTask; // only touched under _initLock
    private Task? _reconnectTask; // only touched under _reconnectSync
    private volatile ConfigMqtt? _lastConfig;
    // The user's own mqtt section as of the last connect. Config changes are compared against this rather
    // than _lastConfig: on the add-on _lastConfig carries the Supervisor host, which the user's (empty)
    // host would never match, so every config.yaml edit would otherwise force a reconnect.
    // Written with Interlocked.Exchange and read with Volatile.Read (see OnConfigChanged for why it is not just volatile).
    private ConfigMqtt? _lastUserConfig;
    // The user's mqtt section from the most recent config reload, whether or not it changed anything. Lets a
    // connect that completes check whether a reload landed while it was in flight; without it, a reload during
    // the *first* connect (no _lastUserConfig yet to compare against) would be lost.
    private ConfigMqtt? _latestUserConfig;
    private volatile string _discoveryTopic = "homeassistant";
    private volatile TaskCompletionSource _connectionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // "Reconnect required" is the difference of two counters rather than a bool, so a config change that
    // lands while a connect is in flight is not lost when that connect completes.
    //  - _configChangesSignalled: Interlocked.Increment from OnConfigChanged (the ConfigLoader's thread).
    //  - _configChangesApplied: Volatile.Write from InitializeClientAsync (under _initLock) after a successful
    //    connect, set to the signalled count read *before* that connect took its config snapshot.
    //  - Both are read with Volatile.Read from publishers (GetClientAsync, EnsureConnectedAsync), the
    //    client's DisconnectedAsync callback (MQTTnet thread) and the reconnect loop (thread pool).
    private int _configChangesSignalled;
    private int _configChangesApplied;

    public MqttConnection(ConfigSource getConfig, Func<IMqttClient> createClient, ILogger? logger)
    {
        _getConfig = getConfig;
        _createClient = createClient;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Back-off between reconnect attempts of the background loop.</summary>
    internal TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(30);

    public event Func<MqttApplicationMessageReceivedEventArgs, Task>? MessageReceived;

    public string DiscoveryTopic => _discoveryTopic;

    internal bool ReconnectRequired =>
        Volatile.Read(ref _configChangesSignalled) != Volatile.Read(ref _configChangesApplied);

    public bool IsReadOnly
    {
        get
        {
            var config = _lastConfig;
            if (config != null)
                return MqttReadOnly.IsReadOnly(config);

            // No successful connect yet (e.g. mocked coordinator): fall back to the client options.
            return MqttReadOnly.IsReadOnlyClientId(_mqttClient?.Options?.ClientId);
        }
    }

    /// <summary>
    /// Called with the user's new mqtt section whenever config.yaml is reloaded. Marks a reconnect as required if
    /// anything that affects the connection changed since the last connect.
    /// </summary>
    public void OnConfigChanged(ConfigMqtt? config)
    {
        var latest = (config ?? new ConfigMqtt()).Clone();

        // Publish this reload, then compare against the last connect. InitializeClientAsync does the mirror
        // image (publish _lastUserConfig, then compare its snapshot against _latestUserConfig). Both writes are
        // full fences, so whichever side runs second sees the other's write: a reload racing a connect is
        // always caught by at least one of them. Catching it twice only bumps the counter twice, which still
        // means a single reconnect.
        Interlocked.Exchange(ref _latestUserConfig, latest);
        if (!HasChanged(Volatile.Read(ref _lastUserConfig), latest)) return;

        _logger.LogInformation("MQTT configuration changed");
        Interlocked.Increment(ref _configChangesSignalled);
    }

    private static bool HasChanged(ConfigMqtt? last, ConfigMqtt? config)
    {
        if (last == null) return false;
        config ??= new ConfigMqtt();
        return !string.Equals(last.Host, config.Host, StringComparison.OrdinalIgnoreCase) ||
               last.Port != config.Port ||
               !string.Equals(last.Username, config.Username) ||
               !string.Equals(last.Password, config.Password) ||
               last.Ssl != config.Ssl ||
               last.Insecure != config.Insecure ||
               !string.Equals(last.CaCertPath, config.CaCertPath) ||
               !string.Equals(last.ClientId, config.ClientId) ||
               last.ReadOnly != config.ReadOnly ||
               // Ordinal: MQTT topics are case-sensitive (unlike the DNS host above), so a case-only edit is a new prefix.
               !string.Equals(last.DiscoveryTopic, config.DiscoveryTopic, StringComparison.Ordinal);
    }

    public async Task<IMqttClient> GetClientAsync(CancellationToken ct = default)
    {
        // Snapshot the volatile field once per check so a concurrent reset cannot turn the
        // "not null" test into a null return.
        var client = _mqttClient;
        if (client != null && !ReconnectRequired)
            return client;

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            client = _mqttClient;
            if (client != null && !ReconnectRequired)
                return client;

            if (ReconnectRequired && client != null)
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
                // and retry once via GetClientAsync() (see PublishAsync).
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
        // Read before the config snapshot: a change signalled after this point keeps ReconnectRequired true.
        var changesBeforeSnapshot = Volatile.Read(ref _configChangesSignalled);

        var (config, userConfig) = await _getConfig().ConfigureAwait(false);
        if (config == null)
            throw new InvalidOperationException("No MQTT server setup, fix your config.yaml");

        var mqttClient = _createClient();
        mqttClient.ApplicationMessageReceivedAsync += OnApplicationMessageReceived;

        var readOnlyReason = MqttReadOnly.GetReason(config);
        switch (readOnlyReason)
        {
            case MqttReadOnly.Reason.ConfigFlag:
                _logger.LogInformation("MQTT read-only mode is active (mqtt.read_only: true); nothing will be published");
                break;
            case MqttReadOnly.Reason.ClientIdHeuristic:
                _logger.LogWarning(
                    "MQTT read-only mode is active because client_id '{ClientId}' contains \"read\". This client-id heuristic is deprecated and will be removed: set mqtt.read_only: true to stay read-only, or choose a client_id without \"read\" to publish",
                    LogSanitizer.StripOrNull(config.ClientId));
                break;
        }

        mqttClient.ConnectedAsync += async _ =>
        {
            _logger.LogInformation("MQTT connected!");

            try
            {
                if (readOnlyReason == MqttReadOnly.Reason.None)
                    await mqttClient.PublishStringAsync(MqttTopics.CompanionStatus, MqttTopics.Online, retain: true).ConfigureAwait(false);

                foreach (var filter in MqttTopics.Subscriptions(config.DiscoveryTopic))
                    await mqttClient.SubscribeAsync(filter).ConfigureAwait(false);
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
                // InitializeClientAsync disposes the client so the next GetClientAsync() builds a fresh one
                // (no reconnect loop is started for it: _mqttClient is only published after success),
                // ReconnectLoopAsync logs and retries after its back-off, EnsureConnectedAsync
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

            if (!ReconnectRequired && args.ClientWasConnected)
                StartReconnectLoop(mqttClient);

            return Task.CompletedTask;
        };

        mqttClient.ConnectingAsync += _ =>
        {
            _logger.LogDebug("MQTT connecting...");
            return Task.CompletedTask;
        };

        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithConfig(config, _logger)
            .WithClientId(config.ClientId)
            .WithCleanSession()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30));

        // A read-only session never publishes "online", so it must not register a retained "offline"
        // will either: an unclean drop would otherwise mark a real, publishing companion as offline.
        if (readOnlyReason == MqttReadOnly.Reason.None)
        {
            optionsBuilder = optionsBuilder
                .WithWillTopic(MqttTopics.CompanionStatus)
                .WithWillRetain()
                .WithWillPayload(MqttTopics.Offline);
        }

        var mqttClientOptions = optionsBuilder.Build();

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
        var userSnapshot = userConfig.Clone();
        Interlocked.Exchange(ref _lastUserConfig, userSnapshot);
        _discoveryTopic = config.DiscoveryTopic;
        Volatile.Write(ref _configChangesApplied, changesBeforeSnapshot);

        // A reload that landed while this connect was in flight may not have been counted by OnConfigChanged
        // (on the first connect it had nothing to compare against). If the latest reload differs from the
        // settings actually used, a reconnect is still due. Compares values, so the reload of an unchanged
        // config at startup does not cause an extra reconnect.
        var latestReload = Volatile.Read(ref _latestUserConfig);
        if (latestReload != null && HasChanged(userSnapshot, latestReload))
        {
            _logger.LogInformation("MQTT configuration changed while connecting; reconnecting with the new settings");
            Interlocked.Increment(ref _configChangesSignalled);
        }

        // Window: a DisconnectedAsync that fired after ConnectedAsync succeeded but before the
        // assignment above failed StartReconnectLoop's ReferenceEquals(_mqttClient, client) guard, so no
        // reconnect loop was started for it. In normal mode the next publish would self-heal through
        // EnsureConnectedAsync, but in read-only mode PublishAsync never gets that far, so start
        // the loop explicitly now (StartReconnectLoop is idempotent under _reconnectSync).
        if (!mqttClient.IsConnected)
        {
            _logger.LogWarning("MQTT connection dropped immediately after connecting; starting reconnect loop");
            StartReconnectLoop(mqttClient);
        }

        return mqttClient;
    }

    private Task OnApplicationMessageReceived(MqttApplicationMessageReceivedEventArgs args)
    {
        var handlers = MessageReceived;
        return handlers == null ? Task.CompletedTask : handlers(args);
    }

    /// <summary>The current background reconnect loop, if one is running (for tests).</summary>
    internal Task? ReconnectTask
    {
        get
        {
            lock (_reconnectSync) return _reconnectTask;
        }
    }

    private void StartReconnectLoop(IMqttClient client)
    {
        lock (_reconnectSync)
        {
            if (!ReferenceEquals(_mqttClient, client))
                return;

            if (_reconnectTask != null && !_reconnectTask.IsCompleted)
                return;

            var shutdownToken = _shutdownCts.Token;
            var reconnectTask = Task.Run(() => ReconnectLoopAsync(client, shutdownToken));
            _reconnectTask = reconnectTask;

            reconnectTask.ContinueWith(_ =>
            {
                lock (_reconnectSync)
                {
                    if (ReferenceEquals(_reconnectTask, reconnectTask))
                        _reconnectTask = null;
                }
            }, TaskScheduler.Default);
        }
    }

    private async Task ReconnectLoopAsync(IMqttClient client, CancellationToken shutdownToken)
    {
        while (!ReconnectRequired && !shutdownToken.IsCancellationRequested)
        {
            if (!ReferenceEquals(_mqttClient, client))
                return;

            if (client.IsConnected)
                return;

            try
            {
                await _connectionLock.WaitAsync(shutdownToken).ConfigureAwait(false);
                try
                {
                    if (client.IsConnected)
                        return;

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
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MQTT reconnect attempt failed, retrying in {Delay} seconds", ReconnectDelay.TotalSeconds);
            }

            try
            {
                await Task.Delay(ReconnectDelay, shutdownToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Host is shutting down; stop retrying.
                return;
            }
        }
    }

    private async Task EnsureConnectedAsync(IMqttClient client)
    {
        if (client.IsConnected || ReconnectRequired)
            return;

        await _connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (client.IsConnected || ReconnectRequired)
                return;

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

    public async Task PublishAsync(string topic, string? payload, bool retain, CancellationToken ct = default)
    {
        // At most one retry: a config reload (GetClientAsync) may dispose the client after we took the
        // reference, which surfaces as ObjectDisposedException; the replacement client is then used.
        for (var attempt = 0; ; attempt++)
        {
            var client = await GetClientAsync(ct).ConfigureAwait(false);

            if (IsReadOnly)
            {
                _logger.LogInformation("ReadOnly, would have sent to {Topic}: {Payload}", LogSanitizer.Strip(topic), LogSanitizer.StripOrNull(payload));
                return;
            }

            try
            {
                await EnsureConnectedAsync(client).ConfigureAwait(false);
                await client.PublishStringAsync(topic, payload, retain: retain, cancellationToken: ct).ConfigureAwait(false);
                return;
            }
            catch (ObjectDisposedException ex) when (attempt == 0)
            {
                _logger.LogDebug(ex, "MQTT client was replaced while publishing to {Topic}; retrying once with the new client", LogSanitizer.Strip(topic));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish MQTT message to {Topic}", LogSanitizer.Strip(topic));
                throw;
            }
        }
    }

    public async Task WaitForConnectionAsync(CancellationToken ct = default)
    {
        var tcs = _connectionTcs;
        await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops any pending reconnect back-off so host shutdown is not held up. The client is intentionally not
    /// disconnected cleanly: letting the socket drop makes the broker publish the "offline" will message.
    /// </summary>
    public void Dispose()
    {
        // The CTS is not disposed on purpose: a reconnect loop may still read the token afterwards.
        _shutdownCts.Cancel();
    }
}
