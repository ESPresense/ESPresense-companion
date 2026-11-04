using ESPresense.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet.Diagnostics.Logger;

namespace ESPresense.Utils;

/// <summary>
/// Forwards MQTTnet's internal log to <see cref="ILogger"/>, with MQTTnet's source in a <c>Source</c> scope property.
/// </summary>
public class MqttNetLogger(ILogger<MqttNetLogger>? logger = null) : IMqttNetLogger
{
    private readonly ILogger _logger = logger ?? NullLogger<MqttNetLogger>.Instance;

    public void Publish(MqttNetLogLevel logLevel, string source, string message, object[] parameters, Exception exception)
    {
        var level = logLevel.ToLogLevel();
        if (!_logger.IsEnabled(level)) return;
        using (_logger.BeginScope(new Dictionary<string, object> { ["Source"] = source }))
#pragma warning disable CA2254 // MQTTnet supplies the template
            _logger.Log(level, exception, message, parameters);
#pragma warning restore CA2254
    }

    public bool IsEnabled { get; set; }
}
