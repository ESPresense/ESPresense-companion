using System;
using System.Collections.Generic;

namespace ESPresense.Weighting;

/// <summary>
/// Epanechnikov kernel, with configurable "bandwidth" via props["bandwidth"].
///
/// Standard form (for -1 < u < 1):
///     K(u) = 0.75 * (1 - u^2)
/// with u = distance / bandwidth.
///
/// If you want it to integrate to 1 over the real line,
/// you can include the division by bandwidth:
///     K(u) = [0.75 * (1 - u^2)] / bandwidth
/// </summary>
public class EpanechnikovKernel : IKernel
{
    private readonly double _bandwidth = 1.0;

    /// <summary>
    /// Create an Epanechnikov kernel with optional bandwidth set in props.
    /// </summary>
    /// <param name="props">Dictionary that may contain {"bandwidth", someValue}</param>
    public EpanechnikovKernel(Dictionary<string, double>? props)
    {
        if (props != null && props.TryGetValue("bandwidth", out var bw) && bw > 0)
        {
            _bandwidth = bw;
        }
    }

    /// <summary>
    /// Evaluate the Epanechnikov kernel at the given distance.
    ///
    /// Returns 0 if |distance / bandwidth| >= 1.
    /// Otherwise, returns the Epanechnikov weight.
    /// </summary>
    /// <param name="distance">Distance from the center</param>
    public double Evaluate(double distance)
    {
        double u = distance / _bandwidth;

        // Outside the range [-1, 1], return 0
        if (Math.Abs(u) >= 1.0)
            return 0.0;

        // Standard Epanechnikov shape
        // If you want to maintain integral=1, also divide by _bandwidth
        double value = 0.75 * (1.0 - (u * u)) / _bandwidth;
        return value;
    }
}