using ESPresense.Models;

namespace ESPresense.Utils;

public record DeviceSearchResult(
    string Id,
    string? Name,
    bool Tracked,
    string? Room,
    string? Floor,
    int Nodes,
    string? NearestNode,
    double? NearestDistance,
    DateTime? LastSeen);

/// <summary>
/// Free-text search over every known device, tracked or not. Powers the Ctrl+K palette, so a bare MAC
/// can be found without fetching (and rendering) the full show-all list.
/// </summary>
public static class DeviceSearch
{
    private static readonly char[] MacSeparators = [':', '-', '.', ' '];

    /// <param name="devices">Every known device.</param>
    /// <param name="query">Whitespace-separated terms, all of which must match. Empty matches everything.</param>
    /// <param name="limit">Maximum results returned.</param>
    /// <param name="nameOf">Resolves the display name (settings can override the advertised one).</param>
    public static List<DeviceSearchResult> Search(IEnumerable<Device> devices, string? query, int limit, Func<Device, string?> nameOf)
    {
        var terms = (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var whole = string.Join(' ', terms);

        var ranked = new List<(Device Device, string? Name, int Tier, int Current, DateTime? LastSeen)>();
        foreach (var device in devices)
        {
            var name = nameOf(device);
            var room = device.Room?.Name;
            var floor = device.Floor?.Name;
            var stripped = Strip(device.Id);

            if (!terms.All(t => Matches(t, device.Id, stripped, name, room, floor)))
                continue;

            var tier = Tier(whole, device.Id, stripped, name);
            ranked.Add((device, name, tier, device.Nodes.Values.Count(n => n.Current), device.LastSeen));
        }

        return ranked
            .OrderBy(r => r.Tier)
            .ThenByDescending(r => r.Device.Track)
            .ThenByDescending(r => r.Current)
            .ThenByDescending(r => r.LastSeen)
            .ThenBy(r => r.Device.Id, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, limit))
            .Select(r => ToResult(r.Device, r.Name))
            .ToList();
    }

    private static bool Matches(string term, string id, string strippedId, string? name, string? room, string? floor)
    {
        if (Contains(id, term) || Contains(name, term) || Contains(room, term) || Contains(floor, term))
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

    private static DeviceSearchResult ToResult(Device device, string? name)
    {
        var current = device.Nodes.Values.Where(n => n.Current).ToList();
        // NaN/Infinity would serialise as a string and break the client's toFixed
        var finite = current.Where(n => double.IsFinite(n.Distance)).ToList();
        var nearest = finite.Count == 0 ? null : finite.MinBy(n => n.Distance);
        return new DeviceSearchResult(
            device.Id,
            name,
            device.Track,
            device.Room?.Name,
            device.Floor?.Name,
            current.Count,
            nearest == null ? null : nearest.Node.Name ?? nearest.Node.Id,
            nearest?.Distance,
            device.LastSeen);
    }

    private static bool Contains(string? haystack, string needle) => haystack?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false;

    private static string Strip(string s) => string.Concat(s.Split(MacSeparators));
}
