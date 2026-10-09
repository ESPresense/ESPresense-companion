using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ESPresense.Services;

/// <summary>
/// Lists ESPresense firmware releases and CI build artifacts (via espresense.com's cached GitHub listings) and resolves
/// them to a trusted download URL for a node, mirroring the UI's firmware picker.
/// </summary>
public class FirmwareCatalogService
{
    private const string ReleasesUrl = "https://espresense.com/releases/list";
    private const string RunsUrl = "https://espresense.com/artifacts/runs";
    private const string Repo = "ESPresense/ESPresense";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private static readonly Regex VersionPattern = new(@"^[A-Za-z0-9._-]+$", RegexOptions.Compiled);
    private static readonly Regex FirmwarePattern = new(@"^[a-z0-9-]+\.bin$", RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly IFirmwareTypeStore _firmwareTypes;
    private readonly NodeTelemetryStore _nodeTelemetryStore;
    private readonly ConcurrentDictionary<string, (DateTime At, JsonElement Json)> _cache = new();

    public FirmwareCatalogService(HttpClient httpClient, IFirmwareTypeStore firmwareTypes, NodeTelemetryStore nodeTelemetryStore)
    {
        _httpClient = httpClient;
        _firmwareTypes = firmwareTypes;
        _nodeTelemetryStore = nodeTelemetryStore;
    }

    public FirmwareTypes? GetTypes() => _firmwareTypes.Get();

    public async Task<IReadOnlyList<FirmwareRelease>> GetReleasesAsync(bool includePrerelease = true, int limit = 10, CancellationToken ct = default)
    {
        var json = await GetJsonAsync(ReleasesUrl, ct);
        return json.EnumerateArray()
            .Where(r => r.GetProperty("assets").GetArrayLength() > 5)
            .Select(r => new FirmwareRelease
            {
                Version = r.GetProperty("tag_name").GetString() ?? "",
                Name = r.GetProperty("name").GetString(),
                Prerelease = r.GetProperty("prerelease").GetBoolean(),
                PublishedAt = r.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String ? p.GetDateTime() : null
            })
            .Where(r => includePrerelease || !r.Prerelease)
            .Take(Math.Clamp(limit, 1, 30))
            .ToList();
    }

    public async Task<IReadOnlyList<FirmwareArtifact>> GetArtifactsAsync(int? pullRequest = null, string? branch = null, int limit = 10, CancellationToken ct = default)
    {
        var json = await GetJsonAsync(RunsUrl, ct);
        return json.GetProperty("workflow_runs").EnumerateArray()
            .Where(r => r.GetProperty("head_repository").GetProperty("full_name").GetString() == Repo)
            .Select(r => new FirmwareArtifact
            {
                ArtifactId = r.GetProperty("id").GetInt64(),
                Branch = r.GetProperty("head_branch").GetString() ?? "",
                PullRequests = r.GetProperty("pull_requests").EnumerateArray().Select(p => p.GetProperty("number").GetInt32()).ToArray(),
                Sha = r.GetProperty("head_sha").GetString() ?? "",
                Title = r.TryGetProperty("head_commit", out var commit) && commit.TryGetProperty("message", out var message)
                    ? message.GetString()?.Split('\n')[0]
                    : null,
                CreatedAt = r.GetProperty("created_at").GetDateTime()
            })
            .Where(r => pullRequest == null || r.PullRequests.Contains(pullRequest.Value))
            .Where(r => string.IsNullOrWhiteSpace(branch) || r.Branch == branch)
            .Take(Math.Clamp(limit, 1, 100))
            .ToList();
    }

    /// <summary>
    /// Builds the download URL for a release version or artifact id. When <paramref name="firmware"/> is omitted the
    /// node's currently reported firmware (CPU + flavor) is used so the update keeps the same build variant.
    /// </summary>
    public (string? Url, string? Firmware, string? Error) ResolveFirmwareUrl(string nodeId, string? version, long? artifactId, string? firmware)
    {
        if (string.IsNullOrWhiteSpace(version) == (artifactId == null))
            return (null, null, "Specify exactly one of version or artifactId");

        if (version != null && !VersionPattern.IsMatch(version))
            return (null, null, "Invalid version");

        if (artifactId is <= 0)
            return (null, null, "Invalid artifactId");

        if (string.IsNullOrWhiteSpace(firmware))
        {
            firmware = _nodeTelemetryStore.Get(nodeId)?.Firmware;
            if (string.IsNullOrWhiteSpace(firmware))
                return (null, null, $"Node '{nodeId}' has not reported its firmware; pass firmware explicitly (see get_firmware_types)");
        }

        if (!firmware.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            firmware += ".bin";

        var known = _firmwareTypes.Get()?.Firmware;
        if (known != null ? known.All(f => f.Name != firmware) : !FirmwarePattern.IsMatch(firmware))
            return (null, null, $"Unknown firmware '{firmware}' (see get_firmware_types)");

        var url = version != null
            ? $"https://github.com/ESPresense/ESPresense/releases/download/{version}/{firmware}"
            : $"https://espresense.com/artifacts/download/runs/{artifactId}/{firmware}";
        return (url, firmware, null);
    }

    private async Task<JsonElement> GetJsonAsync(string url, CancellationToken ct)
    {
        if (_cache.TryGetValue(url, out var hit) && DateTime.UtcNow - hit.At < CacheDuration)
            return hit.Json;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("ESPresense-companion");

        using var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Firmware listing {url} returned {(int)response.StatusCode}");

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var json = doc.RootElement.Clone();
        _cache[url] = (DateTime.UtcNow, json);
        return json;
    }
}

public class FirmwareRelease
{
    public string Version { get; set; } = "";
    public string? Name { get; set; }
    public bool Prerelease { get; set; }
    public DateTime? PublishedAt { get; set; }
}

public class FirmwareArtifact
{
    public long ArtifactId { get; set; }
    public string Branch { get; set; } = "";
    public int[] PullRequests { get; set; } = Array.Empty<int>();
    public string Sha { get; set; } = "";
    public string? Title { get; set; }
    public DateTime CreatedAt { get; set; }
}
