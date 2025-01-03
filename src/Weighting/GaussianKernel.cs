using System;
using System.Collections.Generic;

namespace ESPresense.Weighting
{
    /// <summary>
    /// Gaussian kernel, with configurable "bandwidth" via props["bandwidth"].
    ///
    /// Standard form:
    ///     K(u) = exp(-0.5 * u^2)
    /// with u = distance / bandwidth.
    ///
    /// If you want it to integrate to 1 over the real line,
    /// you can include the division by bandwidth:
    ///     K(u) = [exp(-0.5 * u^2)] / (bandwidth * sqrt(2π))
    /// </summary>
    public class GaussianKernel : IKernel
    {
        private readonly double _bandwidth;
        private readonly double _normalizationFactor;

        /// <summary>
        /// Create a Gaussian kernel with optional bandwidth set in props.
        /// </summary>
        /// <param name="props">Dictionary that may contain {"bandwidth", someValue}</param>
        public GaussianKernel(Dictionary<string, double>? props)
        {
            _bandwidth = 0.5; // Default bandwidth
            if (props != null && props.TryGetValue("bandwidth", out var bw) && bw > 0)
            {
                _bandwidth = bw;
            }
            _normalizationFactor = _bandwidth * Math.Sqrt(2 * Math.PI);
        }

        /// <summary>
        /// Evaluate the Gaussian kernel at the given distance.
        /// </summary>
        /// <param name="distance">Distance from the center</param>
        public double Evaluate(double distance)
        {
            if (double.IsNaN(distance) || double.IsInfinity(distance))
            {
                throw new ArgumentException("Distance must be a finite number.", nameof(distance));
            }

            double u = distance / _bandwidth;
            return Math.Exp(-0.5 * u * u) / _normalizationFactor;
        }
    }
}
