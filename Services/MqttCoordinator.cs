﻿using ESPresense.Events;
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

public class MqttCoordinator
{
    private readonly ILogger<MqttCoordinator> _logger;
    private readonly IServiceProvider _serviceProvider;
    private IManagedMqttClient? _mc;

    public MqttCoordinator(IServiceProvider serviceProvider, ILogger<MqttCoordinator> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        Task.Run(GetClient);
    }

    private async Task<IManagedMqttClient> GetClient()
    {
        var cfg = _serviceProvider.GetRequiredService<ConfigLoader>();
        var c = await cfg.ConfigAsync();

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
                    c.Mqtt.Username = data.Username;
                    c.Mqtt.Password = data.Password;
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

        var mqttFactory = new MqttFactory(_serviceProvider.GetRequiredService<IMqttNetLogger>());

        var mc = mqttFactory.CreateManagedMqttClient();

        var mqttClientOptions =
            new MqttClientOptionsBuilder()
                .WithConfig(c.Mqtt)
                .WithClientId(c.Mqtt.ClientId)
                .WithWillTopic("espresense/companion/status")
                .WithWillRetain()
                .WithWillPayload("offline")
                .Build();

        var managedMqttClientOptions = new ManagedMqttClientOptionsBuilder()
            .WithClientOptions(mqttClientOptions)
            .WithAutoReconnectDelay(TimeSpan.FromSeconds(30))
            .Build();

        await mc.StartAsync(managedMqttClientOptions);
        mc.Options.ConnectionCheckInterval = TimeSpan.FromSeconds(30);
        _mc = mc;

        mc.ConnectedAsync += async s =>
        {
            Log.Information("MQTT {@p} connected", new { primary = true });
            await mc.EnqueueAsync("espresense/companion/status", "online");

            await mc.SubscribeAsync("espresense/devices/+/+");
            await mc.SubscribeAsync("espresense/settings/+/config");
            await mc.SubscribeAsync("espresense/rooms/+/+");
        };

        mc.DisconnectedAsync += s =>
        {
            Log.Information("MQTT {@p} disconnected", new { primary = true });
            return Task.CompletedTask;
        };

        mc.ConnectingFailedAsync += s =>
        {
            Log.Error("MQTT {@p} connection failed {@error}: {@inner}", new { primary = true }, s.Exception.Message, s.Exception?.InnerException?.Message);
            return Task.CompletedTask;
        };

        mc.ApplicationMessageReceivedAsync += OnMqttMessageReceived;

        return mc;
    }

    public event Func<DeviceSettingsEventArgs, Task>? DeviceConfigReceivedAsync;
    public event Func<DeviceMessageEventArgs, Task>? DeviceMessageReceivedAsync;
    public event Func<MqttApplicationMessageReceivedEventArgs, Task>? MqttMessageReceivedAsync;
    public event Func<NodeSettingReceivedEventArgs, Task>? NodeSettingReceivedAsync;
    public event Func<NodeTelemetryReceivedEventArgs, Task>? NodeTelemetryReceivedAsync;
    public event Func<NodeStatusReceivedEventArgs, Task>? NodeStatusReceivedAsync;
    public event EventHandler? MqttMessageMalformed;

    private async Task OnMqttMessageReceived(MqttApplicationMessageReceivedEventArgs arg)
    {
        var parts = arg.ApplicationMessage.Topic.Split('/');
        try
        {
            switch (parts)
            {
                case ["espresense", "rooms", _, "telemetry"]:
                    if (NodeTelemetryReceivedAsync != null)
                    {
                        var ds = JsonConvert.DeserializeObject<NodeTelemetry>(arg.ApplicationMessage.ConvertPayloadToString());
                        if (ds != null) await NodeTelemetryReceivedAsync(new NodeTelemetryReceivedEventArgs { NodeId = parts[2], Payload = ds });
                    }

                    break;
                case ["espresense", "rooms", _, "status"]:
                    var online = arg.ApplicationMessage.ConvertPayloadToString() == "online";
                    NodeStatusReceivedAsync?.Invoke(new NodeStatusReceivedEventArgs { NodeId = parts[2], Online = online });
                    break;
                case ["espresense", "rooms", _, _]:
                {
                    if (NodeSettingReceivedAsync != null)
                        await NodeSettingReceivedAsync(new NodeSettingReceivedEventArgs
                            {
                                NodeId = parts[2],
                                Setting = parts[3],
                                Payload = arg.ApplicationMessage.ConvertPayloadToString()
                            }
                        );
                    break;
                }
                case ["espresense", "devices", _, _]:
                    if (DeviceMessageReceivedAsync != null)
                    {
                        var deviceId = parts[2];
                        var nodeId = parts[3];
                        var deserializeObject = JsonConvert.DeserializeObject<DeviceMessage>(arg.ApplicationMessage.ConvertPayloadToString());
                        if (deserializeObject != null)
                            await DeviceMessageReceivedAsync(new DeviceMessageEventArgs
                            {
                                DeviceId = deviceId,
                                NodeId = nodeId,
                                Payload = deserializeObject
                            });
                    }

                    break;
                case ["espresense", "settings", _, "config"]:
                    if (DeviceConfigReceivedAsync != null)
                    {
                        var ds = JsonConvert.DeserializeObject<DeviceSettings>(arg.ApplicationMessage.ConvertPayloadToString() ?? "");
                        if (ds != null)
                        {
                            ds.OriginalId = parts[2];
                            await DeviceConfigReceivedAsync(new DeviceSettingsEventArgs
                            {
                                DeviceId = parts[2],
                                Payload = ds
                            });
                        }
                    }

                    break;
                default:
                    if (MqttMessageReceivedAsync != null) await MqttMessageReceivedAsync(arg);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parsing mqtt message from {topic}", arg.ApplicationMessage.Topic);
            MqttMessageMalformed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task EnqueueAsync(string topic, string payload, bool retain = false)
    {
        await _mc.EnqueueAsync(topic, payload, retain: retain);
    }
}