using ESPresense.Events;
using ESPresense.Extensions;
using ESPresense.Models;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Diagnostics;
using MQTTnet.Extensions.ManagedClient;
using Newtonsoft.Json;
using System.Collections.Generic;

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

public class MqttCoordinator
{
    private readonly ConfigLoader _cfg;
    private readonly ILogger<MqttCoordinator> _logger;
    private readonly IMqttNetLogger _mqttNetLogger;
    private readonly SupervisorConfigLoader _supervisorConfigLoader;
    private IManagedMqttClient? _mqttClient;
    private readonly SemaphoreSlim _initLock = new SemaphoreSlim(1, 1);
    private Task<IManagedMqttClient>? _initTask;
    private ConfigMqtt? _lastConfig;
    private bool _reconnectRequired;

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
              _lastConfig.Ssl != config.Ssl);
    }

    private async Task<ConfigMqtt?> GetUserConfig()
    {
        var c = await _cfg.ConfigAsync();

        c.Mqtt ??= new ConfigMqtt();

        return string.IsNullOrEmpty(c.Mqtt.Host) ? null : c.Mqtt;
    }

    private async Task<IManagedMqttClient> GetClient()
    {
        // Return existing client if available and no reconnect needed
        if (_mqttClient != null && !_reconnectRequired)
            return _mqttClient;

        await _initLock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (_mqttClient != null && !_reconnectRequired)
                return _mqttClient;

            // Clean up if reconnecting
            if (_reconnectRequired && _mqttClient != null)
            {
                try
                {
                    await _mqttClient.StopAsync();
                    _mqttClient.Dispose();
                    _mqttClient = null;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disconnecting MQTT client during reconnection");
                }
                _initTask = null;
                _reconnectRequired = false;
            }

            // Use cached task if initializing
            if (_initTask != null)
                return await _initTask;

            // Start initialization
            _initTask = InitializeClientAsync();
            return await _initTask;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<IManagedMqttClient> InitializeClientAsync()
    {
        // Get config (try user config first, then supervisor)
        var config = await GetUserConfig() ?? await _supervisorConfigLoader.GetSupervisorConfig();
        if (config == null)
            throw new InvalidOperationException("No MQTT server setup, fix your config.yaml");

        var mqttFactory = new MqttFactory(_mqttNetLogger);

        var mqttClient = mqttFactory.CreateManagedMqttClient();

        var mqttClientOptions =
            new MqttClientOptionsBuilder()
                .WithConfig(config)
                .WithClientId(config.ClientId)
                .WithWillTopic("espresense/companion/status")
                .WithWillRetain()
                .WithWillPayload("offline")
                .WithCleanSession()
                .Build();

        var managedMqttClientOptions = new ManagedMqttClientOptionsBuilder()
            .WithClientOptions(mqttClientOptions)
            .WithAutoReconnectDelay(TimeSpan.FromSeconds(30))
            .Build();

        mqttClient.ConnectedAsync += async s =>
        {
            _logger.LogInformation("MQTT connected!");
            if (!ReadOnly)
                await mqttClient.EnqueueAsync("espresense/companion/status", "online");

            await mqttClient.SubscribeAsync("espresense/devices/+/+");
            await mqttClient.SubscribeAsync("espresense/settings/+/config");
            await mqttClient.SubscribeAsync("espresense/rooms/+/+");
            await mqttClient.SubscribeAsync("espresense/rooms/*/+/set");
            await mqttClient.SubscribeAsync("homeassistant/device_tracker/+/config");
            await mqttClient.SubscribeAsync("espresense/companion/+/attributes");
        };

        mqttClient.DisconnectedAsync += s =>
        {
            _logger.LogInformation("MQTT disconnected");
            return Task.CompletedTask;
        };

        mqttClient.ConnectingFailedAsync += s =>
        {
            _logger.LogError("MQTT connection failed: {Error}",
                s.Exception?.Message + (s.Exception?.InnerException != null ? " - " + s.Exception.InnerException.Message : ""));
            return Task.CompletedTask;
        };

        mqttClient.ApplicationMessageReceivedAsync += OnMqttMessageReceived;

        // Connect
        _logger.LogInformation("Connecting to MQTT at {Host}{Port} as {User}",
            config.Host,
            config.Port.HasValue ? ":" + config.Port : "",
            string.IsNullOrEmpty(config.Username) ? "<anonymous>" : config.Username);

        await mqttClient.StartAsync(managedMqttClientOptions);
        mqttClient.Options.ConnectionCheckInterval = TimeSpan.FromSeconds(30);

        _lastConfig = config.Clone();
        _mqttClient = mqttClient;
        return mqttClient;
    }

    public event Func<DeviceSettingsEventArgs, Task>? DeviceConfigReceivedAsync;
    public event Func<DeviceMessageEventArgs, Task>? DeviceMessageReceivedAsync;
    public event Func<MqttApplicationMessageReceivedEventArgs, Task>? MqttMessageReceivedAsync;
    public event Func<NodeSettingReceivedEventArgs, Task>? NodeSettingReceivedAsync;
    public event Func<NodeTelemetryReceivedEventArgs, Task>? NodeTelemetryReceivedAsync;
    public event Func<NodeStatusReceivedEventArgs, Task>? NodeStatusReceivedAsync;
    public event EventHandler? MqttMessageMalformed;
    public event EventHandler<PreviousDeviceDiscoveredEventArgs>? PreviousDeviceDiscovered;
    public event Func<DeviceAttributesEventArgs, Task>? DeviceAttributesReceivedAsync;

    private async Task OnMqttMessageReceived(MqttApplicationMessageReceivedEventArgs arg)
    {
        var parts = arg.ApplicationMessage.Topic.Split('/');
        var payload = arg.ApplicationMessage.ConvertPayloadToString();

        try
        {
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
                case ["homeassistant", "device_tracker", _, "config"]:
                    await ProcessDiscoveryMessage(arg.ApplicationMessage.Topic, payload);
                    break;
                case ["espresense", "companion", _, "attributes"]:
                    await ProcessDeviceAttributesMessage(parts[2], payload);
                    break;
                default:
                    if (MqttMessageReceivedAsync != null)
                        await MqttMessageReceivedAsync(arg);
                    break;
            }
        }
        catch (JsonSerializationException ex)
        {
            _logger.LogError(ex, "JSON deserialization error for topic {Topic}. Payload: {Payload}",
                arg.ApplicationMessage.Topic, payload);
            MqttMessageMalformed?.Invoke(this, EventArgs.Empty);
        }
        catch (MqttMessageProcessingException ex)
        {
            _logger.LogError(ex, "Error processing {MessageType} message for topic {Topic}. Payload: {Payload}",
                ex.MessageType, ex.Topic, ex.Payload);
            MqttMessageMalformed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error processing message for topic {Topic}. Payload: {Payload}",
                arg.ApplicationMessage.Topic, payload);
            MqttMessageMalformed?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool ReadOnly => _mqttClient?.Options.ClientOptions.ClientId.ToLower().Contains("read") ?? false;

    public virtual async Task EnqueueAsync(string topic, string? payload, bool retain = false)
    {
        var client = await GetClient();

        if (!ReadOnly)
        {
            try
            {
                await client.EnqueueAsync(topic, payload, retain: retain);
            }
            catch (Exception ex)
            {
                var sanitizedTopic = topic.Replace(Environment.NewLine, "").Replace("\n", "").Replace("\r", "");
                _logger.LogError(ex, "Failed to enqueue MQTT message to {Topic}", sanitizedTopic);
                throw;
            }
        }
        else
        {
            var sanitizedTopic = topic.Replace(Environment.NewLine, "").Replace("\n", "").Replace("\r", "");
            _logger.LogInformation("ReadOnly, would have sent to {Topic}: {Payload}", sanitizedTopic, payload);
        }
    }
    
    private async Task ProcessTelemetryMessage(string nodeId, string? payload)
    {
        if (NodeTelemetryReceivedAsync == null || string.IsNullOrEmpty(payload))
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

            await NodeTelemetryReceivedAsync(new NodeTelemetryReceivedEventArgs
            {
                NodeId = nodeId,
                Payload = telemetry
            });
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

    private async Task ProcessStatusMessage(string nodeId, string? payload)
    {
        if (NodeStatusReceivedAsync == null || string.IsNullOrEmpty(payload))
            return;

        var online = payload == "online";
        await NodeStatusReceivedAsync(new NodeStatusReceivedEventArgs
        {
            NodeId = nodeId,
            Online = online
        });
    }

    private async Task ProcessDeviceMessage(string deviceId, string nodeId, string? payload)
    {
        if (DeviceMessageReceivedAsync == null || string.IsNullOrEmpty(payload))
            return;

        try
        {
            var deviceMessage = JsonConvert.DeserializeObject<DeviceMessage>(payload ?? "");
            if (deviceMessage == null)
                throw new MqttMessageProcessingException("Device message was null after deserialization", $"espresense/devices/{deviceId}/{nodeId}", payload, "DeviceMessage");

            await DeviceMessageReceivedAsync(new DeviceMessageEventArgs
            {
                DeviceId = deviceId,
                NodeId = nodeId,
                Payload = deviceMessage
            });
        }
        catch (JsonException ex)
        {
            throw new MqttMessageProcessingException("Failed to parse device message", $"espresense/devices/{deviceId}/{nodeId}", payload, "DeviceMessage", ex);
        }
    }

    private async Task ProcessDeviceConfigMessage(string deviceId, string? payload)
    {
        if (DeviceConfigReceivedAsync == null) return;

        try
        {
            var deviceSettings = JsonConvert.DeserializeObject<DeviceSettings>(payload ?? "");
            if (deviceSettings == null)
                throw new MqttMessageProcessingException("Device settings were null after deserialization", $"espresense/settings/{deviceId}/config", payload, "DeviceConfig");

            deviceSettings.OriginalId = deviceId;
            await DeviceConfigReceivedAsync(new DeviceSettingsEventArgs
            {
                DeviceId = deviceId,
                Payload = deviceSettings
            });
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
        await NodeSettingReceivedAsync(new NodeSettingReceivedEventArgs
        {
            NodeId = nodeId,
            Setting = setting,
            Payload = payload
        });
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

            if (!AutoDiscovery.TryDeserialize(topic, payload, out var msg))
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

            await DeviceAttributesReceivedAsync(new DeviceAttributesEventArgs
            {
                DeviceId = deviceId,
                Attributes = attributes
            });
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