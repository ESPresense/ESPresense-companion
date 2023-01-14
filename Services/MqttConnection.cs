using ESPresense.Extensions;
using ESPresense.Models;
using Flurl.Http;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Diagnostics;
using MQTTnet.Extensions.ManagedClient;
using Serilog;

namespace ESPresense.Services
{
    public class MqttConnectionFactory
    {
        private readonly IServiceProvider _serviceProvider;

        public MqttConnectionFactory(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public async Task<IManagedMqttClient> GetClient(bool primary)
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
                        Log.Warning($"Failed to get MQTT config from Hass.io: {error.Message}");
                    }
                }
                catch (Exception e)
                {
                    Log.Error(e, "Failed to get MQTT config from Hass.io");
                }
            }

            var mqttFactory = new MqttFactory(_serviceProvider.GetRequiredService<IMqttNetLogger>());

            var mc = mqttFactory.CreateManagedMqttClient();

            var mqttClientOptions =
                primary
                    ? new MqttClientOptionsBuilder()
                        .WithConfig(c.Mqtt)
                        .WithClientId("espresense-companion")
                        .WithWillTopic("espresense/companion/status")
                        .WithWillRetain()
                        .WithWillPayload("offline")
                        .Build()
                    : new MqttClientOptionsBuilder()
                        .WithConfig(c.Mqtt)
                        .Build();

            var managedMqttClientOptions = new ManagedMqttClientOptionsBuilder()
                .WithClientOptions(mqttClientOptions)
                .Build();

            await mc.StartAsync(managedMqttClientOptions);

            mc.ConnectedAsync += async (s) =>
            {
                Log.Information("MQTT connected {primary}", primary);
                if (primary) await mc.EnqueueAsync("espresense/companion/status", "online");
            };

            mc.ConnectingFailedAsync += (s) =>
            {
                Log.Error("MQTT connection failed {@error}: {@inner}", s.Exception.Message, s.Exception?.InnerException?.Message);
                return Task.CompletedTask;
            };
            return mc;
        }
    }
}