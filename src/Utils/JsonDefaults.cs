using System.Text.Json;
using System.Text.Json.Serialization;
using ESPresense.Converters;

namespace ESPresense.Utils;

/// <summary>
/// The System.Text.Json options for each boundary the Companion owns (#1846). Use these instead of
/// constructing options per call. They differ from each other on purpose, because each one reproduces what
/// that boundary has always emitted; making them agree would be a wire-format change for its own issue.
/// </summary>
public static class JsonDefaults
{
    /// <summary>
    /// REST API (<c>/api/...</c>) and, since #1849, MCP tools and resources, which return exactly the REST JSON:
    /// web defaults (camelCase, case-insensitive reads), enums as strings, NaN and infinities as named literals.
    /// ASP.NET owns its own instance; Program.cs applies <see cref="ConfigureWeb"/> to it.
    /// </summary>
    public static JsonSerializerOptions Web { get; } = ReadOnly(ConfigureWeb(new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    /// <summary>
    /// WebSocket events and commands (<c>/ws</c>): web defaults with named floating-point literals.
    /// Unlike <see cref="Web"/>, enums are numbers.
    /// </summary>
    public static JsonSerializerOptions WebSocket { get; } = ReadOnly(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    });

    /// <summary>
    /// What the Companion publishes to MQTT (discovery, telemetry, device settings), byte-for-byte what Newtonsoft
    /// produced with the old <c>SerializerSettings.NullIgnore</c>: camelCase, null and default values omitted, public
    /// fields included, Newtonsoft's string escaping and double formatting.
    /// </summary>
    public static JsonSerializerOptions Mqtt { get; } = ReadOnly(LikeNewtonsoft(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        IncludeFields = true
    }));

    /// <summary>
    /// Like <see cref="Mqtt"/> but nothing is omitted and names are as declared: what Newtonsoft's default settings
    /// produced. For the payloads that used <c>JsonConvert.SerializeObject</c> without settings: the MQTT
    /// <c>last_seen</c> stamp and the firmware-update progress frames on its WebSocket.
    /// </summary>
    public static JsonSerializerOptions Verbatim { get; } = ReadOnly(LikeNewtonsoft(new JsonSerializerOptions()));

    /// <summary>
    /// Reading JSON other publishers wrote to MQTT, as leniently as Newtonsoft did: names matched case-insensitively,
    /// comments and trailing commas allowed, numbers and booleans accepted for string members.
    /// </summary>
    public static JsonSerializerOptions MqttRead { get; } = ReadOnly(new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new NewtonsoftCompatible.LenientStringConverter() }
    });

    /// <summary>Applies the <see cref="Web"/> settings to an options instance someone else owns (ASP.NET's).</summary>
    public static JsonSerializerOptions ConfigureWeb(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter());
        options.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
        return options;
    }

    private static JsonSerializerOptions LikeNewtonsoft(JsonSerializerOptions options)
    {
        options.Encoder = NewtonsoftCompatible.Encoder.Instance;
        options.Converters.Add(new NewtonsoftCompatible.DoubleConverter());
        return options;
    }

    private static JsonSerializerOptions ReadOnly(JsonSerializerOptions options)
    {
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
