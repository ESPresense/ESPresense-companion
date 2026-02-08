using ESPresense.Models;
using MQTTnet;
using MQTTnet.Formatter;

namespace ESPresense.Extensions;

public static class MqttClientOptionsBuilderExtensions
{
    public static MqttClientOptionsBuilder WithConfig(this MqttClientOptionsBuilder mcob, ConfigMqtt mqtt)
    {
        mcob
            .WithTcpServer(mqtt.Host ?? "localhost", mqtt.Port)
            .WithCredentials(mqtt.Username, mqtt.Password)
            .WithProtocolVersion(MqttProtocolVersion.V311);
        if (mqtt.Ssl != null)
            mcob.WithTlsOptions(o =>
            {
                o.UseTls(mqtt.Ssl ?? false);
                o.WithAllowUntrustedCertificates();
            });
        return mcob;
    }
}
