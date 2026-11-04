namespace ESPresense.Services;

/// <summary>
/// Every MQTT topic string the coordinator subscribes to or publishes on, in one place. These are wire
/// format: nodes, Home Assistant and other companions depend on them, so changing a value is a breaking change.
/// </summary>
internal static class MqttTopics
{
    public const string Root = "espresense";

    /// <summary>Retained companion availability ("online"/"offline"); also the last-will topic.</summary>
    public const string CompanionStatus = "espresense/companion/status";
    public const string Online = "online";
    public const string Offline = "offline";

    /// <summary>Lease coordination between companions: <c>espresense/companion/lease/{lease}</c>.</summary>
    public const string CompanionLeasePrefix = "espresense/companion/lease/";

    /// <summary>The broadcast node id used in <c>espresense/rooms/*/{setting}/set</c>.</summary>
    public const string AllNodes = "*";

    // Segment names used by the router.
    public const string RoomsSegment = "rooms";
    public const string DevicesSegment = "devices";
    public const string SettingsSegment = "settings";
    public const string CompanionSegment = "companion";
    public const string TelemetrySegment = "telemetry";
    public const string StatusSegment = "status";
    public const string UpdateErrorSegment = "update_error";
    public const string SetSegment = "set";
    public const string ConfigSegment = "config";
    public const string AttributesSegment = "attributes";
    public const string DeviceTrackerComponent = "device_tracker";

    public static string NodeTelemetry(string nodeId) => $"espresense/rooms/{nodeId}/telemetry";
    public static string NodeStatus(string nodeId) => $"espresense/rooms/{nodeId}/status";
    public static string NodeUpdateError(string nodeId) => $"espresense/rooms/{nodeId}/update_error";
    public static string NodeSetting(string nodeId, string setting) => $"espresense/rooms/{nodeId}/{setting}";
    public static string NodeSettingSet(string nodeId, string setting) => $"espresense/rooms/{nodeId}/{setting}/set";
    public static string DeviceMessage(string deviceId, string nodeId) => $"espresense/devices/{deviceId}/{nodeId}";
    public static string DeviceSettings(string deviceId) => $"espresense/settings/{deviceId}/config";
    public static string DeviceAttributes(string deviceId) => $"espresense/companion/{deviceId}/attributes";
    public static string DeviceTrackerDiscoveryFilter(string discoveryTopic) => $"{discoveryTopic}/device_tracker/+/config";

    /// <summary>Every topic under one node, for clearing its retained state: <c>espresense/rooms/{node}/#</c>.</summary>
    public static string NodeAllTopicsFilter(string nodeId) => $"espresense/rooms/{nodeId}/#";

    /// <summary>Companion-published room state of a tracked device (the HA device_tracker state topic).</summary>
    public static string CompanionDeviceState(string deviceId) => $"espresense/companion/{deviceId}";

    /// <summary>Companion telemetry (non-retained).</summary>
    public const string CompanionTelemetry = "espresense/companion/telemetry";

    public static string CompanionLease(string leaseName) => CompanionLeasePrefix + leaseName;

    /// <summary>Home Assistant discovery config: <c>{prefix}/{component}/{discovery id}/config</c>.</summary>
    public static string DiscoveryConfig(string discoveryTopic, string component, string discoveryId) => $"{discoveryTopic}/{component}/{discoveryId}/config";

    /// <summary>
    /// The subscriptions made on every (re)connect, in order. <c>espresense/rooms/*/+/set</c> is a literal
    /// <c>*</c> node id (broadcast settings), not an MQTT wildcard.
    /// </summary>
    public static IReadOnlyList<string> Subscriptions(string discoveryTopic) =>
    [
        "espresense/devices/+/+",
        "espresense/settings/+/config",
        "espresense/rooms/+/+",
        "espresense/rooms/*/+/set",
        DeviceTrackerDiscoveryFilter(discoveryTopic),
        "espresense/companion/+/attributes",
        CompanionLeasePrefix + "+",
    ];
}
