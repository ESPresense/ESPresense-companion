namespace ESPresense.Utils;

static class GpsUtil
{
    private const double R = 6378137; // Earth's mean radius in meters

    public static (double? lat, double? lon) Add(double? x, double? y, double? lat, double? lon)
    {
        var dLat = x / R;
        var dLon = y / (R * Math.Cos(Math.PI * lat / 180 ?? 0));

        return (lat: lat + dLat * 180 / Math.PI, lon: lon + dLon * 180 / Math.PI);
    }
}