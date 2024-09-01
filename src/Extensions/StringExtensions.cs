namespace ESPresense.Extensions;

public static class StringExtensions
{
    public static int? ToInt(this string? str, int? defaultValue = default)
    {
        return int.TryParse(str, out var result) ? result : defaultValue;
    }
}