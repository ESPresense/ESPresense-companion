using ESPresense.Events;
using ESPresense.Extensions;
using ESPresense.Models;
using Flurl.Http;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Diagnostics;
using MQTTnet.Extensions.ManagedClient;
using Newtonsoft.Json;
using Serilog;

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
    private IManagedMqttClient? _mqttClient;

    public MqttCoordinator(ConfigLoader cfg, ILogger<MqttCoordinator> logger, IMqttNetLogger mqttNetLogger)
    {
        _cfg = cfg;
        _logger = logger;
        _mqttNetLogger = mqttNetLogger;
        Task.Run(GetClient);
    }

    private async Task<IManagedMqttClient> GetClient()
    {
        var c = await _cfg.ConfigAsync();

        c.Mqtt ??= new ConfigMqtt();

        var supervisorToken = Environment.GetEnvironmentVariable("SUPERVISOR_TOKEN");
        if (string.IsNullOrEmpty(c.Mqtt.Host) && !string.IsNullOrEmpty(supervisorToken))
            try
            {
                try
                {
                    var (_, _, data) = await "http://supervisor/services/mqtt"
                        .WithOAuthBearerToken(supervisorToken)
                        .GetJsonAsync<HassIoResult>();

                    c.Mqtt.Host = string.IsNullOrEmpty(data.Host) ? "localhost" : data.Host;
                    c.Mqtt.Port = int.TryParse(data.Port, out var i) ? i : null;
                    if (!string.IsNullOrEmpty(data.Username)) c.Mqtt.Username = data.Username;
                    if (!string.IsNullOrEmpty(data.Password)) c.Mqtt.Password = data.Password;
                    c.Mqtt.Ssl = data.Ssl;
                }
                catch (FlurlHttpException ex)
                {
                    var error = await ex.GetResponseJsonAsync<HassIoResult>();
                    Log.Warning($"Failed to get MQTT config from Hass Supervisor: {error.Message}");
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to get MQTT config from Hass Supervisor");
            }

        var mqttFactory = new MqttFactory(_mqttNetLogger);

        var mqttClient = mqttFactory.CreateManagedMqttClient();

        var mqttClientOptions =
            new MqttClientOptionsBuilder()
                .WithConfig(c.Mqtt)
                .WithClientId(c.Mqtt.ClientId)
                .WithWillTopic("espresense/companion/status")
                .WithWillRetain()
                .WithWillPayload("offline")
                .WithCleanSession()
                .Build();

        var managedMqttClientOptions = new ManagedMqttClientOptionsBuilder()
            .WithClientOptions(mqttClientOptions)
            .WithAutoReconnectDelay(TimeSpan.FromSeconds(30))
            .Build();

        await mqttClient.StartAsync(managedMqttClientOptions);
        mqttClient.Options.ConnectionCheckInterval = TimeSpan.FromSeconds(30);
        _mqttClient = mqttClient;

        Log.Logger.Information("Attempting to connect to mqtt server at " + (c.Mqtt.Port != null ? "{@host}:{@port}" : "{@host}") + " as {@username}...", c.Mqtt.Host ?? "localhost", c.Mqtt.Port, c.Mqtt.Username ?? "<anonymous>");

        mqttClient.ConnectedAsync += async s =>
        {
            Log.Information("MQTT connected!");
            if (!ReadOnly)
                await mqttClient.EnqueueAsync("espresense/companion/status", "online");

            await mqttClient.SubscribeAsync("espresense/devices/+/+");
            await mqttClient.SubscribeAsync("espresense/settings/+/config");
            await mqttClient.SubscribeAsync("espresense/rooms/+/+");
            await mqttClient.SubscribeAsync("homeassistant/device_tracker/+/config");
        };

        mqttClient.DisconnectedAsync += s =>
        {
            Log.Information("MQTT disconnected");
            return Task.CompletedTask;
        };

        mqttClient.ConnectingFailedAsync += s =>
        {
            Log.Error("MQTT connection failed {@error}: {@inner}", new { primary = true }, s.Exception.Message, s.Exception?.InnerException?.Message);
            return Task.CompletedTask;
        };

        mqttClient.ApplicationMessageReceivedAsync += OnMqttMessageReceived;

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

    public async Task EnqueueAsync(string topic, string? payload, bool retain = false)
    {
        if (!ReadOnly)
        {
            await _mqttClient.EnqueueAsync(topic, payload, retain: retain);
        } else {
            Log.Information("ReadOnly, would have sent to " + topic + ": " + payload);
        }
    }

    private async Task ProcessTelemetryMessage(string nodeId, string? payload)
    {
        if (NodeTelemetryReceivedAsync == null) return;

        try
        {
            var telemetry = JsonConvert.DeserializeObject<NodeTelemetry>(payload ?? "");
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
        if (NodeStatusReceivedAsync == null) return;

        if (payload == null)
            throw new MqttMessageProcessingException("Status payload was null", $"espresense/rooms/{nodeId}/status", null, "Status");

        var online = payload == "online";
        await NodeStatusReceivedAsync(new NodeStatusReceivedEventArgs
        {
            NodeId = nodeId,
            Online = online
        });
    }

    private async Task ProcessDeviceMessage(string deviceId, string nodeId, string? payload)
    {
        if (DeviceMessageReceivedAsync == null) return;

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
}