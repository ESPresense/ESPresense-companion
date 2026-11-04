using System.Text.Json;
using System.Text.Json.Serialization;

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

    /// <summary>Applies the <see cref="Web"/> settings to an options instance someone else owns (ASP.NET's).</summary>
    public static JsonSerializerOptions ConfigureWeb(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter());
        options.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
        return options;
    }

    private static JsonSerializerOptions ReadOnly(JsonSerializerOptions options)
    {
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
