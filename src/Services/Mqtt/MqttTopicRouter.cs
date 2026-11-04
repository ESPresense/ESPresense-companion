namespace ESPresense.Services;

/// <summary>A recognised inbound topic, with the ids parsed out of it. <see cref="Topic"/> is the topic as received.</summary>
internal abstract record MqttRoute(string Topic);

/// <summary><c>espresense/rooms/{node}/telemetry</c></summary>
internal sealed record NodeTelemetryRoute(string Topic, string NodeId) : MqttRoute(Topic);

/// <summary><c>espresense/rooms/{node}/status</c></summary>
internal sealed record NodeStatusRoute(string Topic, string NodeId) : MqttRoute(Topic);

/// <summary><c>espresense/rooms/{node}/update_error</c></summary>
internal sealed record NodeUpdateErrorRoute(string Topic, string NodeId) : MqttRoute(Topic);

/// <summary><c>espresense/rooms/{node}/{setting}</c>, or the broadcast <c>espresense/rooms/*/{setting}/set</c>.</summary>
internal sealed record NodeSettingRoute(string Topic, string NodeId, string Setting) : MqttRoute(Topic);

/// <summary><c>espresense/devices/{device}/{node}</c></summary>
internal sealed record DeviceMessageRoute(string Topic, string DeviceId, string NodeId) : MqttRoute(Topic);

/// <summary><c>espresense/settings/{device}/config</c></summary>
internal sealed record DeviceSettingsRoute(string Topic, string DeviceId) : MqttRoute(Topic);

/// <summary><c>espresense/companion/{device}/attributes</c></summary>
internal sealed record DeviceAttributesRoute(string Topic, string DeviceId) : MqttRoute(Topic);

/// <summary><c>{discovery prefix}/device_tracker/{discovery id}/config</c></summary>
internal sealed record DiscoveryRoute(string Topic, string DiscoveryId) : MqttRoute(Topic);

/// <summary>
/// Maps an inbound topic to a <see cref="MqttRoute"/>. Pure: no I/O, no state beyond the discovery prefix.
/// A <c>null</c> match means "not ours": the coordinator hands such messages to <c>MqttMessageReceivedAsync</c>.
/// </summary>
internal sealed class MqttTopicRouter
{
    public MqttTopicRouter(string discoveryTopic)
    {
        DiscoveryTopic = discoveryTopic;
    }

    /// <summary>The Home Assistant discovery prefix this router matches (<c>mqtt.discovery_topic</c>).</summary>
    public string DiscoveryTopic { get; }

    public MqttRoute? Match(string topic)
    {
        var parts = topic.Split('/');

        // Checked first, so a discovery prefix of "espresense" still routes discovery messages as before.
        if (parts is [var prefix, MqttTopics.DeviceTrackerComponent, var discoveryId, MqttTopics.ConfigSegment] && prefix == DiscoveryTopic)
            return new DiscoveryRoute(topic, discoveryId);

        return parts switch
        {
            [MqttTopics.Root, MqttTopics.RoomsSegment, var node, MqttTopics.TelemetrySegment] => new NodeTelemetryRoute(topic, node),
            [MqttTopics.Root, MqttTopics.RoomsSegment, var node, MqttTopics.StatusSegment] => new NodeStatusRoute(topic, node),
            [MqttTopics.Root, MqttTopics.RoomsSegment, var node, MqttTopics.UpdateErrorSegment] => new NodeUpdateErrorRoute(topic, node),
            [MqttTopics.Root, MqttTopics.RoomsSegment, MqttTopics.AllNodes, var setting, MqttTopics.SetSegment] => new NodeSettingRoute(topic, MqttTopics.AllNodes, setting),
            [MqttTopics.Root, MqttTopics.RoomsSegment, var node, var setting] => new NodeSettingRoute(topic, node, setting),
            [MqttTopics.Root, MqttTopics.DevicesSegment, var device, var node] => new DeviceMessageRoute(topic, device, node),
            [MqttTopics.Root, MqttTopics.SettingsSegment, var device, MqttTopics.ConfigSegment] => new DeviceSettingsRoute(topic, device),
            [MqttTopics.Root, MqttTopics.CompanionSegment, var device, MqttTopics.AttributesSegment] => new DeviceAttributesRoute(topic, device),
            _ => null
        };
    }
}
