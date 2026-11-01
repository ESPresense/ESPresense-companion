using System.Globalization;
using ESPresense.Models;

namespace ESPresense.Utils;

/// <summary>One row of the Ctrl+K palette. <see cref="Kind"/> tells the client which page it opens.</summary>
public record SearchResult(string Kind, string Id, string? Name, string Detail, string? Badge);

/// <summary>Anything the palette can find. To add a kind, add a factory below and a route in the client's urls.ts.</summary>
/// <param name="Result">What the client shows.</param>
/// <param name="Also">Other text a term may match, besides the id and name (room, floor...).</param>
/// <param name="Group">Breaks ties between kinds: lower comes first.</param>
/// <param name="Prominent">Listed first within its tier and group (tracked, online...).</param>
/// <param name="Activity">Then highest first (nodes hearing a device...).</param>
/// <param name="Recent">Then most recent first.</param>
public record Searchable(SearchResult Result, string?[] Also, int Group, bool Prominent = false, int Activity = 0, DateTime? Recent = null);

/// <summary>
/// Free-text search over everything known, tracked or not. Powers the Ctrl+K palette, so a bare MAC can be
/// found without fetching (and rendering) the full show-all list.
/// </summary>
public static class Search
{
    private static readonly char[] MacSeparators = [':', '-', '.', ' '];

    /// <param name="items">Everything searchable.</param>
    /// <param name="query">Whitespace-separated terms, all of which must match. Empty matches everything.</param>
    /// <param name="limit">Maximum results returned.</param>
    public static List<SearchResult> Find(IEnumerable<Searchable> items, string? query, int limit)
    {
        var terms = (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var whole = string.Join(' ', terms);

        var ranked = new List<(Searchable Item, int Tier)>();
        foreach (var item in items)
        {
            var (id, name) = (item.Result.Id, item.Result.Name);
            var stripped = Strip(id);

            if (!terms.All(t => Matches(t, id, stripped, name, item.Also)))
                continue;

            ranked.Add((item, Tier(whole, id, stripped, name)));
        }

        return ranked
            .OrderBy(r => r.Tier)
            .ThenBy(r => r.Item.Group)
            .ThenByDescending(r => r.Item.Prominent)
            .ThenByDescending(r => r.Item.Activity)
            .ThenByDescending(r => r.Item.Recent)
            .ThenBy(r => r.Item.Result.Id, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, limit))
            .Select(r => r.Item.Result)
            .ToList();
    }

    public static Searchable Device(Device device, string? name)
    {
        var current = device.Nodes.Values.Where(n => n.Current).ToList();
        var result = new SearchResult("device", device.Id, name, DeviceDetail(device, current), device.Track ? null : "untracked");
        return new Searchable(result, [device.Room?.Name, device.Floor?.Name], 1, device.Track, current.Count, device.LastSeen);
    }

    public static Searchable Node(NodeStateTele node)
    {
        var result = new SearchResult("node", node.Id ?? "", node.Name, node.Online ? "online" : "offline", "node");
        return new Searchable(result, node.Floors ?? [], 0, node.Online);
    }

    private static string DeviceDetail(Device device, List<DeviceToNode> current)
    {
        var parts = new List<string>();
        if (device.Room?.Name is { } room) parts.Add(room);
        if (current.Count == 0)
        {
            parts.Add("not heard recently");
            return string.Join(" · ", parts);
        }

        parts.Add($"{current.Count} nodes");
        var nearest = current.Where(n => double.IsFinite(n.Distance)).MinBy(n => n.Distance);
        if (nearest != null)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"nearest {nearest.Node.Name ?? nearest.Node.Id} {nearest.Distance:F2} m"));
        return string.Join(" · ", parts);
    }

    private static bool Matches(string term, string id, string strippedId, string? name, string?[] also)
    {
        if (Contains(id, term) || Contains(name, term) || also.Any(a => Contains(a, term)))
            return true;

        // "a4:c1:38" should find "a4c138a4048c" and vice versa
        var strippedTerm = Strip(term);
        return strippedTerm.Length > 0 && strippedId.Contains(strippedTerm, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>0 = exact id/name, 1 = id/name starts with the query, 2 = anything else that matched.</summary>
    private static int Tier(string query, string id, string strippedId, string? name)
    {
        if (query.Length == 0) return 2;
        var strippedQuery = Strip(query);
        if (id.Equals(query, StringComparison.OrdinalIgnoreCase) || (name?.Equals(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (strippedQuery.Length > 0 && strippedId.Equals(strippedQuery, StringComparison.OrdinalIgnoreCase)))
            return 0;

        if (id.StartsWith(query, StringComparison.OrdinalIgnoreCase) || (name?.StartsWith(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (strippedQuery.Length > 0 && strippedId.StartsWith(strippedQuery, StringComparison.OrdinalIgnoreCase)))
            return 1;
        return 2;
    }

    private static bool Contains(string? haystack, string needle) => haystack?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false;

    private static string Strip(string s) => string.Concat(s.Split(MacSeparators));
}
