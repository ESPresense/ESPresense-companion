using System.Text.RegularExpressions;

namespace ESPresense.Utils;

public static class ColorUtils
{
    // D3 schemeCategory10 palette to match frontend defaults
    private static readonly string[] Palette =
    {
        "#1F77B4", "#FF7F0E", "#2CA02C", "#D62728", "#9467BD",
        "#8C564B", "#E377C2", "#7F7F7F", "#BCBD22", "#17BECF"
    };

    public static string AssignColor(string? key)
    {
        var idx = Math.Abs(HashString(key ?? string.Empty)) % Palette.Length;
        return Palette[idx];
    }

    public static bool IsValidHex(string? color)
    {
        if (string.IsNullOrWhiteSpace(color)) return false;
        return Regex.IsMatch(color.Trim(), "^#([0-9a-fA-F]{6})$");
    }

    public static string? NormalizeHex(string? color)
    {
        if (string.IsNullOrWhiteSpace(color)) return null;
        var c = color.Trim();
        // Allow no-# formats too, like "1f77b4" or "fff"
        if (System.Text.RegularExpressions.Regex.IsMatch(c, "^([0-9a-fA-F]{3})$"))
        {
            c = "#" + c;
        }
        else if (System.Text.RegularExpressions.Regex.IsMatch(c, "^([0-9a-fA-F]{6})$"))
        {
            c = "#" + c;
        }
        if (Regex.IsMatch(c, "^#([0-9a-fA-F]{3})$"))
        {
            // Expand #RGB to #RRGGBB
            var r = c[1]; var g = c[2]; var b = c[3];
            return $"#{r}{r}{g}{g}{b}{b}".ToUpperInvariant();
        }
        if (Regex.IsMatch(c, "^#([0-9a-fA-F]{6})$"))
        {
            return c.ToUpperInvariant();
        }
        return null;
    }

    private static int HashString(string str)
    {
        // Same style as JS: hash = (hash << 5) - hash + char
        int hash = 0;
        foreach (var ch in str)
        {
            hash = (hash << 5) - hash + ch;
        }
        return hash;
    }
}

