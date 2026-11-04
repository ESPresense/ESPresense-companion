using System.Text.Json;
using System.Text.Json.Serialization;

namespace ESPresense.Utils;

/// <summary>
/// The REST API's JSON settings, applied in Program.cs and reused by the wire-format tests so a change here
/// shows up in their pinned output.
/// </summary>
public static class RestJson
{
    /// <summary>Applies the REST settings on top of the web defaults ASP.NET Core starts from.</summary>
    public static void Configure(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter());
        options.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
    }

    /// <summary>A fresh copy of the options REST responses are written with.</summary>
    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Configure(options);
        return options;
    }
}
