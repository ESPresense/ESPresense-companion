using Microsoft.Extensions.Logging;
using Flurl.Http;
using ESPresense.Models;
using System.Text.Json.Serialization;

namespace ESPresense.Services
{
    public class SupervisorConfigLoader
    {


        public record HassIoResult(
            [property: JsonPropertyName("result")] string Result,
            [property: JsonPropertyName("message")] string Message,
            [property: JsonPropertyName("data")] HassIoMqtt Data
        );


        public record HassIoMqtt(
            [property: JsonPropertyName("addon")] string Addon,
            [property: JsonPropertyName("host")] string Host,
            [property: JsonPropertyName("port")] string Port,
            [property: JsonPropertyName("ssl")] bool Ssl,
            [property: JsonPropertyName("username")] string Username,
            [property: JsonPropertyName("password")] string Password,
            [property: JsonPropertyName("protocol")] string Protocol
        );

        private readonly ILogger<SupervisorConfigLoader> _logger;
        private ConfigMqtt? _cachedConfig;
        private readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);
        private Task<ConfigMqtt?>? _loadingTask;

        public SupervisorConfigLoader(ILogger<SupervisorConfigLoader> logger)
        {
            _logger = logger;
        }

        public async Task<ConfigMqtt?> GetSupervisorConfig()
        {
            // Early out if token isn't set.
            var token = Environment.GetEnvironmentVariable("SUPERVISOR_TOKEN");
            if (string.IsNullOrEmpty(token))
            {
                _logger.LogDebug("No SUPERVISOR_TOKEN found in environment variables");
                return null;
            }

            // Return cached config if available.
            if (_cachedConfig != null)
            {
                return _cachedConfig;
            }

            await _cacheLock.WaitAsync();
            try
            {
                // Double-check after acquiring lock.
                if (_cachedConfig != null)
                {
                    return _cachedConfig;
                }

                // If a loading task is already in progress, await it.
                if (_loadingTask != null)
                {
                    return await _loadingTask;
                }

                // Start a new loading task.
                _loadingTask = LoadConfigAsync(token);
                var result = await _loadingTask;
                _loadingTask = null;
                return result;
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        private async Task<ConfigMqtt?> LoadConfigAsync(string token)
        {
            try
            {
                _logger.LogDebug("Fetching MQTT configuration from Home Assistant Supervisor");
                var (_, _, data) = await "http://supervisor/services/mqtt"
                    .WithOAuthBearerToken(token)
                    .GetJsonAsync<HassIoResult>();

                var config = new ConfigMqtt
                {
                    Host = string.IsNullOrEmpty(data.Host) ? "localhost" : data.Host,
                    Port = int.TryParse(data.Port, out var i) ? i : null,
                    Username = data.Username,
                    Password = data.Password,
                    Ssl = data.Ssl
                };

                // Cache the config.
                _cachedConfig = config;
                _logger.LogInformation("Successfully retrieved MQTT configuration from Supervisor");

                return config;
            }
            catch (FlurlHttpException ex) when (ex.Call.Response.StatusCode == 400)
            {
                _logger.LogWarning("Hass supervisor says the mqtt add-on is not installed");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get MQTT config from Hass Supervisor");
                return null;
            }
        }
    }
}
