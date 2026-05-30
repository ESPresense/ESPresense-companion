using System;

namespace ESPresense.Simulation.AccuracyHarness;

/// <summary>
/// Mirrors the firmware log-distance path-loss formula used by
/// <c>BleFingerprint::calculateDistance</c>:
///     dist = pow(10, (calRssi - rssi) / (10.0 * absorption))
/// (<c>absorption &lt;= 0</c> falls back to free-space n=2.0).
///
/// The harness synthesizes RSSI samples from a ground-truth (x,y) position,
/// injects noise on the dB side, and then runs the same formula the
/// firmware uses on-device. This is the whole point of using the firmware
/// model here: it preserves the log-distance asymmetry so the
/// room-misidentification rate is honest.
/// </summary>
public static class FirmwareDistanceModel
{
    public static double RssiToDistance(double calRssi, double rssi, double absorption)
    {
        double n = absorption > 0.0 ? absorption : 2.0;
        return Math.Pow(10.0, (calRssi - rssi) / (10.0 * n));
    }

    public static double DistanceToRssi(double calRssi, double distanceM, double absorption)
    {
        if (distanceM <= 0.0) return calRssi;
        double n = absorption > 0.0 ? absorption : 2.0;
        return calRssi - 10.0 * n * Math.Log10(distanceM);
    }
}
