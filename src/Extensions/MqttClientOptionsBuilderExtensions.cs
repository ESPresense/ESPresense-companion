using ESPresense.Models;
using MQTTnet.Client;
using Serilog;

namespace ESPresense.Extensions;

public static class MqttClientOptionsBuilderExtensions
{
    public static MqttClientOptionsBuilder WithConfig(this MqttClientOptionsBuilder mcob, ConfigMqtt mqtt)
    {
        Log.Logger.Information("Connecting to mqtt server at " + (mqtt.Port != null ? "{@host}:{@port}" : "{@host}") + "...", mqtt.Host ?? "localhost", mqtt.Port);

        mcob
            .WithTcpServer(mqtt.Host ?? "localhost", mqtt.Port)
            .WithCredentials(mqtt.Username, mqtt.Password);
        if (mqtt.Ssl != null)
            mcob.WithTlsOptions(o =>
            {
                o.UseTls(mqtt.Ssl ?? false);
                o.WithAllowUntrustedCertificates();
            });
        return mcob;
    }
}