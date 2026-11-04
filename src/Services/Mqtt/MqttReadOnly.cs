using ESPresense.Models;

namespace ESPresense.Services;

/// <summary>
/// Decides MQTT read-only mode. Kept apart from both <see cref="MqttConnection"/> and <see cref="MqttCoordinator"/>
/// so the deprecated client-id heuristic lives in one place for the release that removes it.
/// </summary>
internal static class MqttReadOnly
{
    /// <summary>Why a config is (or is not) read-only.</summary>
    internal enum Reason
    {
        None,
        /// <summary><c>mqtt.read_only: true</c> in config.</summary>
        ConfigFlag,
        /// <summary>Deprecated heuristic: the client id contains "read".</summary>
        ClientIdHeuristic
    }

    /// <summary>
    /// The explicit <see cref="ConfigMqtt.ReadOnly"/> flag wins; otherwise the deprecated client-id heuristic is
    /// applied for backward compatibility (and <see cref="MqttConnection"/> logs a deprecation warning when it decides).
    /// </summary>
    public static Reason GetReason(ConfigMqtt config)
    {
        if (config.ReadOnly) return Reason.ConfigFlag;
        if (IsReadOnlyClientId(config.ClientId)) return Reason.ClientIdHeuristic;
        return Reason.None;
    }

    public static bool IsReadOnly(ConfigMqtt config) => GetReason(config) != Reason.None;

    /// <summary>
    /// Deprecated heuristic kept for backward compatibility: a client id containing "read" (case-insensitive)
    /// means read-only. Use <see cref="ConfigMqtt.ReadOnly"/>.
    /// </summary>
    public static bool IsReadOnlyClientId(string? clientId) =>
        clientId?.Contains("read", StringComparison.OrdinalIgnoreCase) ?? false;
}
