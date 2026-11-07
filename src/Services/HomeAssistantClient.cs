using System.Text.Json;
using System.Text.Json.Nodes;
using Flurl.Http;

namespace ESPresense.Services;

/// <summary>
/// Reads Home Assistant entity state. As an add-on it goes through the Supervisor proxy with its token;
/// elsewhere it needs HASS_URL and HASS_TOKEN in the environment.
/// </summary>
public class HomeAssistantClient
{
    private readonly string? _baseUrl;
    private readonly string? _token;

    public HomeAssistantClient()
    {
        var supervisor = Environment.GetEnvironmentVariable("SUPERVISOR_TOKEN");
        if (!string.IsNullOrEmpty(supervisor))
        {
            _baseUrl = "http://supervisor/core/api";
            _token = supervisor;
        }
        else
        {
            _baseUrl = Environment.GetEnvironmentVariable("HASS_URL")?.TrimEnd('/') is { Length: > 0 } url ? url + "/api" : null;
            _token = Environment.GetEnvironmentVariable("HASS_TOKEN");
        }
    }

    public bool Available => _baseUrl != null && !string.IsNullOrEmpty(_token);

    /// <summary>The entity's attributes, or null when it does not exist or Home Assistant is unreachable.</summary>
    public virtual async Task<JsonObject?> GetAttributesAsync(string entityId, CancellationToken ct = default)
    {
        if (!Available) return null;
        var json = await $"{_baseUrl}/states/{Uri.EscapeDataString(entityId)}"
            .WithOAuthBearerToken(_token!)
            .WithTimeout(TimeSpan.FromSeconds(10))
            .GetStringAsync(cancellationToken: ct);
        return JsonNode.Parse(json)?["attributes"] as JsonObject;
    }
}
