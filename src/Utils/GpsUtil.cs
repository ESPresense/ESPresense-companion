using ESPresense.Models; // Need this for ConfigGps
using System; // Need this for Math functions

namespace ESPresense.Utils;

static class GpsUtil
{
    private const double R = 6378137; // Earth's mean radius in meters

    // Updated method signature to accept ConfigGps
    public static (double? lat, double? lon) Add(double? x, double? y, ConfigGps gpsConfig)
    {
        // Check for null inputs
        if (!x.HasValue || !y.HasValue || !gpsConfig.Latitude.HasValue || !gpsConfig.Longitude.HasValue)
        {
            return (null, null);
        }

        var lat = gpsConfig.Latitude.Value;
        var lon = gpsConfig.Longitude.Value;
        var rotationDeg = gpsConfig.Rotation ?? 0.0; // Default to 0 if null

        // Convert rotation from degrees to radians
        var rotationRad = rotationDeg * Math.PI / 180.0;

        // Rotate the coordinates (counter-clockwise rotation of point for clockwise system rotation)
        // x' = x * cos(theta) + y * sin(theta)
        // y' = -x * sin(theta) + y * cos(theta)
        var cosTheta = Math.Cos(rotationRad);
        var sinTheta = Math.Sin(rotationRad);
        var xRotated = x.Value * cosTheta + y.Value * sinTheta;
        var yRotated = -x.Value * sinTheta + y.Value * cosTheta;

        // Calculate latitude change (North/South) based on rotated Y
        var dLat = yRotated / R;

        // Calculate longitude change (East/West) based on rotated X, adjusted for latitude
        var dLon = xRotated / (R * Math.Cos(Math.PI * lat / 180.0));

        // Return the new latitude and longitude
        return (lat: lat + dLat * 180.0 / Math.PI, lon: lon + dLon * 180.0 / Math.PI);
    }
}