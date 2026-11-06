using System.Reflection;
using ESPresense.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using StjIgnore = System.Text.Json.Serialization.JsonIgnoreAttribute;
using StjName = System.Text.Json.Serialization.JsonPropertyNameAttribute;

namespace ESPresense.Companion.Tests.Conventions;

/// <summary>
/// Newtonsoft configured as the MQTT boundary had it before #1846/#1854, kept as the reference the System.Text.Json
/// options are compared against. The models no longer carry Newtonsoft attributes, so this resolver maps the
/// System.Text.Json ones back: <c>[JsonPropertyName]</c> and <c>[JsonIgnore]</c> are what <c>[JsonProperty]</c> and
/// Newtonsoft's <c>[JsonIgnore]</c> were, and when reading, <see cref="MqttNameAttribute"/> is the old <c>[JsonProperty]</c>
/// name of a member whose API name differs.
/// </summary>
internal sealed class NewtonsoftReference(bool reading, bool camelCase) : DefaultContractResolver
{
    /// <summary>Formerly <c>SerializerSettings.NullIgnore</c>: what the Companion published.</summary>
    public static JsonSerializerSettings Publish { get; } = new()
    {
        DefaultValueHandling = DefaultValueHandling.Ignore,
        NullValueHandling = NullValueHandling.Ignore,
        ContractResolver = new NewtonsoftReference(reading: false, camelCase: true)
    };

    /// <summary><c>JsonConvert.DeserializeObject&lt;T&gt;</c> with default settings: how inbound MQTT was parsed.</summary>
    public static JsonSerializerSettings Parse { get; } = new()
    {
        ContractResolver = new NewtonsoftReference(reading: true, camelCase: false)
    };

    protected override string ResolvePropertyName(string propertyName) =>
        camelCase ? new CamelCaseNamingStrategy().GetPropertyName(propertyName, false) : propertyName;

    protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
    {
        var property = base.CreateProperty(member, memberSerialization);
        if (member.GetCustomAttribute<StjIgnore>() != null) property.Ignored = true;
        var name = (reading ? member.GetCustomAttribute<MqttNameAttribute>()?.Name : null) ?? member.GetCustomAttribute<StjName>()?.Name;
        if (name != null) property.PropertyName = name;
        return property;
    }
}
