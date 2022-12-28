using MQTTnet.Diagnostics;
using Serilog;

namespace ESPresense.Extensions;

public class MqttNetLogger : IMqttNetLogger
{
    public void Publish(MqttNetLogLevel logLevel, string source, string message, object[] parameters, Exception exception)
    {
        Log.ForContext("Source", source).Write(logLevel.ToSerilog(), exception, message, parameters);
    }

    public bool IsEnabled { get; set; }
}