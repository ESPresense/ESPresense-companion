using MQTTnet.Diagnostics;

namespace ESPresense.Extensions
{
    public static class MqttNetLogLevelExtensions
    {
        public static  Serilog.Events.LogEventLevel ToSerilog(this MqttNetLogLevel level)
        {
            return level switch
            {
                MqttNetLogLevel.Error => Serilog.Events.LogEventLevel.Error,
                MqttNetLogLevel.Warning => Serilog.Events.LogEventLevel.Warning,
                MqttNetLogLevel.Info => Serilog.Events.LogEventLevel.Information,
                MqttNetLogLevel.Verbose => Serilog.Events.LogEventLevel.Verbose,
                _ => Serilog.Events.LogEventLevel.Debug
            };
        }
    }
}
