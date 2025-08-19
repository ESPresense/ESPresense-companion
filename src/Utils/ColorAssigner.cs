using ESPresense.Models;

namespace ESPresense.Utils;

public static class ColorAssigner
{
    // Default palette (D3 schemeCategory10) if none provided via config.map.colors
    private static readonly string[] DefaultPalette =
    {
        "#1F77B4", "#FF7F0E", "#2CA02C", "#D62728", "#9467BD",
        "#8C564B", "#E377C2", "#7F7F7F", "#BCBD22", "#17BECF"
    };

    private const double Tolerance = 1e-6;

    public static void AssignRoomColors(Config config)
    {
        if (config?.Floors == null) return;

        // Use default D3 schemeCategory10 palette
        var palette = DefaultPalette;

        foreach (var floor in config.Floors)
        {
            var rooms = floor.Rooms?.ToList() ?? new List<ConfigRoom>();
            if (rooms.Count == 0) continue;

            // Normalize any provided colors first
            foreach (var r in rooms)
            {
                r.Color = ColorUtils.NormalizeHex(r.Color);
            }

            // Build adjacency graph (index-based)
            var adj = new Dictionary<int, HashSet<int>>();
            for (int i = 0; i < rooms.Count; i++) adj[i] = new HashSet<int>();

            for (int i = 0; i < rooms.Count; i++)
            {
                for (int j = i + 1; j < rooms.Count; j++)
                {
                    if (AreAdjacent(rooms[i], rooms[j]))
                    {
                        adj[i].Add(j);
                        adj[j].Add(i);
                    }
                }
            }

            // Order rooms by degree (desc), but keep explicit colors fixed
            var order = Enumerable.Range(0, rooms.Count)
                .OrderByDescending(idx => adj[idx].Count)
                .ToList();

            // Assign colors greedily with hashed starting index to diversify palette usage
            foreach (var idx in order)
            {
                var room = rooms[idx];
                if (!string.IsNullOrEmpty(room.Color)) continue; // Respect explicit color

                // Colors already used by neighbors
                var neighborColors = new HashSet<string>(adj[idx]
                    .Select(n => rooms[n].Color)
                    .Where(c => !string.IsNullOrEmpty(c))!
                    .Select(c => c!));

                // Start at deterministic index based on room id/name to spread palette usage
                var key = room.Id ?? room.Name ?? string.Empty;
                var startIdx = palette.Length == 0 ? 0 : Math.Abs(Hash(key)) % palette.Length;
                string? choice = null;
                for (int offset = 0; offset < palette.Length; offset++)
                {
                    var candidate = palette[(startIdx + offset) % palette.Length];
                    if (!neighborColors.Contains(candidate))
                    {
                        choice = candidate;
                        break;
                    }
                }

                // If all palette colors are used by neighbors, pick the color maximizing distance to neighbors
                if (choice == null)
                {
                    double BestScore(string cand) => neighborColors.Count == 0 ? double.MaxValue : neighborColors.Min(nc => RgbDistance(cand, nc));
                    var src = palette.Length > 0 ? palette : DefaultPalette;
                    choice = src.OrderByDescending(BestScore).First();
                }

                room.Color = choice!;
            }
        }
    }

    // No configurable palette; using DefaultPalette

    private static bool AreAdjacent(ConfigRoom a, ConfigRoom b)
    {
        var pa = a.Points ?? Array.Empty<double[]>();
        var pb = b.Points ?? Array.Empty<double[]>();

        if (pa.Length < 2 || pb.Length < 2) return false;

        // Check for shared edge: segment endpoints match or segments overlap. Include closing edges.
        int La = pa.Length;
        int Lb = pb.Length;
        for (int i = 0; i < La; i++)
        {
            var a1 = pa[i];
            var a2 = pa[(i + 1) % La];
            for (int j = 0; j < Lb; j++)
            {
                var b1 = pb[j];
                var b2 = pb[(j + 1) % Lb];

                if ((PointsEqual(a1, b1) && PointsEqual(a2, b2)) || (PointsEqual(a1, b2) && PointsEqual(a2, b1)))
                    return true; // shared full edge

                if (SegmentsOverlap(a1, a2, b1, b2))
                    return true; // partial overlap or T-junction on the same line
            }
        }

        // As a fallback, consider adjacency if any vertex coincides
        foreach (var va in pa)
            foreach (var vb in pb)
                if (PointsEqual(va, vb))
                    return true;

        return false;
    }

    private static bool PointsEqual(double[] a, double[] b)
    {
        return Math.Abs(a[0] - b[0]) <= Tolerance && Math.Abs(a[1] - b[1]) <= Tolerance;
    }

    private static double RgbDistance(string hex1, string hex2)
    {
        (int r, int g, int b) c1 = HexToRgb(hex1);
        (int r, int g, int b) c2 = HexToRgb(hex2);
        var dr = c1.r - c2.r;
        var dg = c1.g - c2.g;
        var db = c1.b - c2.b;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }

    private static (int r, int g, int b) HexToRgb(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Length < 7) return (255, 255, 255);
        return (Convert.ToInt32(hex.Substring(1, 2), 16), Convert.ToInt32(hex.Substring(3, 2), 16), Convert.ToInt32(hex.Substring(5, 2), 16));
    }

    private static int Hash(string str)
    {
        int hash = 0;
        foreach (var ch in str)
        {
            hash = (hash << 5) - hash + ch;
        }
        return hash;
    }

    private static bool SegmentsOverlap(double[] a1, double[] a2, double[] b1, double[] b2)
    {
        // Check if two line segments are colinear and overlap with more than a single point.
        double ax = a2[0] - a1[0], ay = a2[1] - a1[1];
        double bx = b2[0] - b1[0], by = b2[1] - b1[1];

        // Cross product near zero => parallel
        double cross = ax * by - ay * bx;
        if (Math.Abs(cross) > Tolerance) return false;

        // Check if they lie on the same line (b1 with respect to a1->a2)
        double cross2 = (b1[0] - a1[0]) * ay - (b1[1] - a1[1]) * ax;
        if (Math.Abs(cross2) > Tolerance) return false;

        // Project onto the dominant axis and test interval overlap
        if (Math.Abs(ax) >= Math.Abs(ay))
        {
            // Use x
            double aMin = Math.Min(a1[0], a2[0]), aMax = Math.Max(a1[0], a2[0]);
            double bMin = Math.Min(b1[0], b2[0]), bMax = Math.Max(b1[0], b2[0]);
            double overlap = Math.Min(aMax, bMax) - Math.Max(aMin, bMin);
            return overlap > Tolerance;
        }
        else
        {
            // Use y
            double aMin = Math.Min(a1[1], a2[1]), aMax = Math.Max(a1[1], a2[1]);
            double bMin = Math.Min(b1[1], b2[1]), bMax = Math.Max(b1[1], b2[1]);
            double overlap = Math.Min(aMax, bMax) - Math.Max(aMin, bMin);
            return overlap > Tolerance;
        }
    }
}
