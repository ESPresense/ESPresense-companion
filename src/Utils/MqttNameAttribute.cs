using System.Reflection;
using System.Text.Json.Serialization.Metadata;

namespace ESPresense.Utils;

/// <summary>
/// The member's name in JSON read from MQTT, when it differs from the name every other boundary uses.
/// Only <see cref="JsonDefaults.MqttRead"/> honours it, so a model that is parsed from a node's payload and also
/// returned by the API (<see cref="Models.NodeTelemetry"/>: the node sends <c>ver</c>, the API has always said
/// <c>version</c>) keeps both names. A model only ever read from MQTT uses <c>[JsonPropertyName]</c> instead.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class MqttNameAttribute(string name) : Attribute
{
    public string Name { get; } = name;

    /// <summary>Type-info modifier that renames members carrying the attribute.</summary>
    internal static void Apply(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object) return;
        foreach (var property in typeInfo.Properties)
            if (property.AttributeProvider?.GetCustomAttributes(typeof(MqttNameAttribute), true).FirstOrDefault() is MqttNameAttribute mqtt)
                property.Name = mqtt.Name;
    }
}
