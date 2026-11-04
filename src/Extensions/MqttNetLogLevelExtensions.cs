using Microsoft.Extensions.Logging;
using MQTTnet.Diagnostics.Logger;

namespace ESPresense.Extensions
{
    public static class MqttNetLogLevelExtensions
    {
        public static LogLevel ToLogLevel(this MqttNetLogLevel level)
        {
            return level switch
            {
                MqttNetLogLevel.Error => LogLevel.Error,
                MqttNetLogLevel.Warning => LogLevel.Warning,
                MqttNetLogLevel.Info => LogLevel.Information,
                MqttNetLogLevel.Verbose => LogLevel.Trace,
                _ => LogLevel.Debug
            };
        }
    }
}
