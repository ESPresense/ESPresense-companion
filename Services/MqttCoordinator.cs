using ESPresense.Extensions;
using ESPresense.Models;
using Flurl.Http;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Diagnostics;
using MQTTnet.Extensions.ManagedClient;
using Serilog;
using System.Collections.Concurrent;
using Newtonsoft.Json;

namespace ESPresense.Services;

public class MqttCoordinator
{
    private readonly IServiceProvider _serviceProvider;
    private IManagedMqttClient? _mc;
    private readonly ConcurrentQueue<string> _pendingSubscriptions = new ConcurrentQueue<string>();

    public MqttCoordinator(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        Task.Run(() => GetClient());
    }


    private async Task<IManagedMqttClient> GetClient()
    {
        var cfg = _serviceProvider.GetRequiredService<ConfigLoader>();
        var c = await cfg.ConfigAsync();

        c.Mqtt ??= new ConfigMqtt();

        var supervisorToken = Environment.GetEnvironmentVariable("SUPERVISOR_TOKEN");
        if (string.IsNullOrEmpty(c.Mqtt.Host) && !string.IsNullOrEmpty(supervisorToken))
        {
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

        mc.ConnectedAsync += async (s) =>
        {
            Log.Information("MQTT {@p} connected", new { primary = true });
            await mc.EnqueueAsync("espresense/companion/status", "online");

            while (_pendingSubscriptions.TryDequeue(out var topic)) await SubscribeToTopicAsync(topic);
        };

        mc.DisconnectedAsync += (s) =>
        {
            Log.Information("MQTT {@p} disconnected", new { primary = true });
            return Task.CompletedTask;
        };

        mc.ConnectingFailedAsync += (s) =>
        {
            Log.Error("MQTT {@p} connection failed {@error}: {@inner}", new { primary = true }, s.Exception.Message, s.Exception?.InnerException?.Message);
            return Task.CompletedTask;
        };

        mc.ApplicationMessageReceivedAsync += OnMqttMessageReceived;

        return mc;
    }

    public event Func<DeviceMessageEventArgs, Task>? DeviceMessageReceivedAsync;

    public event Func<MqttApplicationMessageReceivedEventArgs, Task>? MqttMessageReceivedAsync;

    private async Task OnMqttMessageReceived(MqttApplicationMessageReceivedEventArgs e)
    {
        var parts = e.ApplicationMessage.Topic.Split('/');

        if (parts is ["espresense", "devices", _, _])
        {
            var deviceId = parts[2];
            var nodeId = parts[3];
            if (DeviceMessageReceivedAsync != null)
            {
                var deserializeObject = JsonConvert.DeserializeObject<DeviceMessage>(e.ApplicationMessage.ConvertPayloadToString());
                if (deserializeObject != null)
                {
                    await DeviceMessageReceivedAsync(new DeviceMessageEventArgs
                    {
                        DeviceId = deviceId,
                        NodeId = nodeId,
                        Payload = deserializeObject
                    });
                }
            }
        }
        else
        {
            if (MqttMessageReceivedAsync != null) await MqttMessageReceivedAsync(e);
        }
    }

    public async Task SubscribeAsync(string topic)
    {
        if (_mc is { IsConnected: true })
        {
            await SubscribeToTopicAsync(topic);
        }
        else
        {
            _pendingSubscriptions.Enqueue(topic);
        }
    }

    private async Task SubscribeToTopicAsync(string topic)
    {
        await _mc.SubscribeAsync(topic);
    }

    public async Task EnqueueAsync(string topic, string payload, bool retain = false)
    {
        await _mc.EnqueueAsync(topic, payload, retain: retain);
    }
}

public class DeviceMessageEventArgs
{
    public string DeviceId { get; set; }
    public string NodeId { get; set; }
    public DeviceMessage Payload { get; set; }
}