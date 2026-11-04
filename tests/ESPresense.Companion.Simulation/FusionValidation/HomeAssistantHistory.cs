using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace ESPresense.Simulation.FusionValidation;

/// <summary>
/// One state-change row from Home Assistant's <c>/api/history/period</c> response.
/// </summary>
public sealed record HaStateChange(string EntityId, string State, DateTimeOffset LastChanged);

/// <summary>
/// Pulls (or replays) Home Assistant history for the entities the fusion validation
/// harness needs: room occupancy binary sensors and tagged-device trackers.
///
/// Live pulls require HA_URL/HA_TOKEN (not committed anywhere). Every pull can be
/// cached to a JSON file with <see cref="SaveToFile"/> so later runs — including CI —
/// replay the same ground truth without hitting HA again, per ESPA-463's acceptance
/// criteria ("without re-deriving ground truth by hand each time").
/// </summary>
public static class HaHistoryClient
{
    public static async Task<Dictionary<string, List<HaStateChange>>> FetchAsync(
        string baseUrl,
        string token,
        IEnumerable<string> entityIds,
        DateTimeOffset start,
        DateTimeOffset end,
        HttpClient? http = null)
    {
        var client = http ?? new HttpClient();
        var ids = string.Join(",", entityIds);
        var startStr = Uri.EscapeDataString(start.ToString("O", CultureInfo.InvariantCulture));
        var endStr = Uri.EscapeDataString(end.ToString("O", CultureInfo.InvariantCulture));
        var url = $"{baseUrl.TrimEnd('/')}/api/history/period/{startStr}?filter_entity_id={ids}&end_time={endStr}&minimal_response";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Authorization", $"Bearer {token}");
        using var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        return Parse(body);
    }

    public static Dictionary<string, List<HaStateChange>> LoadFromFile(string path) =>
        Parse(File.ReadAllText(path));

    public static void SaveToFile(string path, Dictionary<string, List<HaStateChange>> history)
    {
        var root = new JsonArray();
        foreach (var series in history.Values)
        {
            var arr = new JsonArray();
            foreach (var sc in series.OrderBy(s => s.LastChanged))
            {
                arr.Add(new JsonObject
                {
                    ["entity_id"] = sc.EntityId,
                    ["state"] = sc.State,
                    ["last_changed"] = sc.LastChanged.ToString("O", CultureInfo.InvariantCulture)
                });
            }
            root.Add(arr);
        }
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// HA's history endpoint returns an array-of-arrays: one inner array per entity,
    /// each entry a state-change object with entity_id/state/last_changed.
    /// </summary>
    private static Dictionary<string, List<HaStateChange>> Parse(string json)
    {
        var result = new Dictionary<string, List<HaStateChange>>();
        var root = JsonNode.Parse(json) as JsonArray
            ?? throw new FormatException("expected a JSON array of per-entity history arrays");

        foreach (var seriesNode in root)
        {
            if (seriesNode is not JsonArray series) continue;
            // minimal_response (see FetchAsync) carries entity_id only on the first row of each series --
            // every other row has just state/last_changed. Without carrying the id forward, Parse used to
            // drop almost every transition from a live pull while a round-tripped (SaveToFile'd) file hid
            // the bug by writing entity_id on every row. Rows before the first entity_id in a series are
            // still skipped, which matches HA's actual output (entity_id is always on the first row).
            string? seriesEntityId = null;
            foreach (var rowNode in series)
            {
                if (rowNode is not JsonObject row) continue;
                var entityId = row["entity_id"]?.GetValue<string>() ?? seriesEntityId;
                seriesEntityId = entityId;
                var state = row["state"]?.GetValue<string>();
                var lastChanged = row["last_changed"]?.GetValue<string>();
                if (entityId is null || state is null || lastChanged is null) continue;

                var change = new HaStateChange(entityId, state, DateTimeOffset.Parse(lastChanged, CultureInfo.InvariantCulture));
                if (!result.TryGetValue(entityId, out var list))
                    result[entityId] = list = new List<HaStateChange>();
                list.Add(change);
            }
        }

        foreach (var list in result.Values)
            list.Sort((a, b) => a.LastChanged.CompareTo(b.LastChanged));

        return result;
    }
}
