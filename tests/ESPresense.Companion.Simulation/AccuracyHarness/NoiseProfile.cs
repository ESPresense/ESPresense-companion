using System;

namespace ESPresense.Simulation.AccuracyHarness;

/// <summary>
/// Reproducible RSSI noise model. Noise is applied on the dB side, then
/// fed through <see cref="FirmwareDistanceModel"/> to produce a measured
/// distance — matching the firmware codepath.
///
/// Modeling Gaussian noise on the meter side instead would understate the
/// risk of room misidentification near boundaries (#1817), which is the
/// failure mode this harness is meant to keep honest.
/// </summary>
public sealed class NoiseProfile
{
    public double GaussianStdDb { get; init; } = 2.5;
    public double MultipathProbability { get; init; } = 0.10;
    public double MultipathAttenuationDb { get; init; } = 6.0;

    public double Sample(Random rng, double trueRssi)
    {
        double noisy = trueRssi + Gauss(rng) * GaussianStdDb;
        if (rng.NextDouble() < MultipathProbability)
            noisy -= MultipathAttenuationDb;
        return noisy;
    }

    private static double Gauss(Random rng)
    {
        // Box-Muller. Matches Python's random.gauss(0, 1) in distribution,
        // not byte-for-byte (intentionally — see docs/accuracy.md).
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}
