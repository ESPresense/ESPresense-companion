namespace ESPresense.Extensions;

public static class RelativeTimer
{
    private static readonly Lazy<DateTime> ApplicationStartTime = new Lazy<DateTime>(() => DateTime.UtcNow);
    private static TimeSpan? NullableSubtract(DateTime? a, DateTime? b) => a == null || b == null ? null : a.Value - b.Value;

    public static double? RelativeMilliseconds(this DateTime? utc)
    {
        return NullableSubtract(utc, ApplicationStartTime.Value)?.TotalMilliseconds;
    }

    public static double RelativeMilliseconds(this DateTime utc)
    {
        return (utc - ApplicationStartTime.Value).TotalMilliseconds;
    }
}