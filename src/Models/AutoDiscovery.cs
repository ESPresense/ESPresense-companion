using ESPresense.Services;
using ESPresense.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;
using TextExtensions;

namespace ESPresense.Models;

public class AutoDiscovery
{
    private readonly ILogger _logger;
    private bool _sent;

    public DiscoveryRecord Message { get; set; }
    public string DiscoveryId { get; internal set; }
    public string Component { get; internal set; }

    public AutoDiscovery(string component, Device dev, string? discoveryId = null, string? sourceType = null, ILogger<AutoDiscovery>? logger = null)
    {
        _logger = logger ?? NullLogger<AutoDiscovery>.Instance;
        DiscoveryId = discoveryId ?? $"espresense-{dev.Id}".ToSnakeCase();
        Component = component;

        Message = new DiscoveryRecord()
        {
            Name = dev.Name,
            UniqueId = $"espresense-companion-{dev.Id}",
            StateTopic = MqttTopics.CompanionDeviceState(dev.Id),
            JsonAttributesTopic = MqttTopics.DeviceAttributes(dev.Id),
            EntityStatusTopic = MqttTopics.CompanionStatus,
            Device = new DeviceRecord()
            {
                Name = dev.Name ?? dev.Id,
                Manufacturer = "ESPresense",
                Model = "Companion",
                SwVersion = "1.0.0",
                Identifiers = new[] { $"espresense-{dev.Id}" }
            },
            Origin = new OriginRecord { Name = "ESPresense Companion" },
            SourceType = sourceType
        };
    }

    public AutoDiscovery(string component, string discoveryId, DiscoveryRecord message, ILogger<AutoDiscovery>? logger = null)
    {
        _logger = logger ?? NullLogger<AutoDiscovery>.Instance;
        DiscoveryId = discoveryId;
        Component = component;
        Message = message;
    }

    public async Task Send(IMqttCoordinator mqtt)
    {
        if (_sent) return;
        _sent = true;

        _logger.LogDebug("[+] Discovery {Component} {DiscoveryId}", Component, DiscoveryId);
        await mqtt.EnqueueAsync(MqttTopics.DiscoveryConfig(mqtt.DiscoveryTopic, Component, DiscoveryId),
            MqttPayloads.Discovery(Message), true);
    }

    public async Task Delete(IMqttCoordinator mqtt)
    {
        _logger.LogDebug("[-] Discovery {Component} {DiscoveryId}", Component, DiscoveryId);
        await mqtt.EnqueueAsync(MqttTopics.DiscoveryConfig(mqtt.DiscoveryTopic, Component, DiscoveryId), null, true);
    }

    internal static bool TryDeserialize(string topic, string payload, out AutoDiscovery? msg, string discoveryTopic = "homeassistant", ILogger<AutoDiscovery>? logger = null)
    {
        logger ??= NullLogger<AutoDiscovery>.Instance;
        var parts = topic.Split("/");
        if (parts.Length != 4 || parts[0] != discoveryTopic || parts[3] != "config")
        {
            logger.LogDebug("Invalid topic structure");
            msg = null;
            return false;
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            msg = null;
            return false;
        }

        try
        {
            var discovery = JsonSerializer.Deserialize<DiscoveryRecord>(payload, JsonDefaults.MqttRead);
            if (discovery == null)
            {
                logger.LogWarning("Failed to deserialize discovery message");
                msg = null;
                return false;
            }

            if (discovery?.Origin?.Name != "ESPresense Companion")
            {
                var origin = discovery?.Origin?.Name;
                if (origin != null) logger.LogDebug("Ignoring discovery message from origin: {Origin}", origin);
                msg = null;
                return false;
            }

            msg = new AutoDiscovery(parts[1], parts[2], discovery, logger);
            return true;
        }
        catch (JsonException ex)
        {
            logger.LogWarning("Failed to deserialize discovery message: {Message}", ex.Message);
            msg = null;
            return false;
        }
    }

    public class DiscoveryRecord
    {
        [JsonPropertyName("name")] public string? Name { get; set; }

        [JsonPropertyName("unique_id")] public string? UniqueId { get; set; }

        [JsonPropertyName("state_topic")] public string? StateTopic { get; set; }

        [JsonPropertyName("json_attributes_topic")] public string? JsonAttributesTopic { get; set; }

        [JsonPropertyName("availability_topic")] public string? EntityStatusTopic { get; set; }

        [JsonPropertyName("device")] public DeviceRecord? Device { get; set; }

        [JsonPropertyName("origin")] public OriginRecord? Origin { get; set; }

        [JsonPropertyName("source_type")] public string? SourceType { get; set; }
    }

    public class DeviceRecord
    {
        [JsonPropertyName("name")] public string? Name { get; set; }

        [JsonPropertyName("manufacturer")] public string? Manufacturer { get; set; }

        [JsonPropertyName("model")] public string? Model { get; set; }

        [JsonPropertyName("sw_version")] public string? SwVersion { get; set; }

        [JsonPropertyName("identifiers")] public string[]? Identifiers { get; set; }
    }

    public class OriginRecord
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
    }
}