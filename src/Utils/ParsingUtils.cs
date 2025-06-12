namespace ESPresense.Utils;

public static class ParsingUtils
{
    public static double? ParseDoubleOrDefault(string? value)
    {
        return double.TryParse(value, out var v) ? v : null;
    }

    public static int? ParseIntOrDefault(string? value)
    {
        return int.TryParse(value, out var v) ? v : null;
    }
}
