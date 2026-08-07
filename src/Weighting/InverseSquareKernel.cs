using System;
using System.Collections.Generic;

namespace ESPresense.Weighting
{
    /// <summary>
    /// Inverse Square Kernel: K(u) = 1 / (u^2 + epsilon)
    /// </summary>
    public class InverseSquareKernel : IKernel
    {
        private readonly double _epsilon;

        /// <summary>
        /// Create an Inverse Square Kernel with optional epsilon set in props.
        /// </summary>
        /// <param name="props">Dictionary that may contain {"epsilon", someValue}</param>
        public InverseSquareKernel(Dictionary<string, double>? props = null)
        {
            // Default epsilon value
            _epsilon = props != null && props.TryGetValue("epsilon", out var eps) && eps > 0
                ? eps
                : 1e-6; // A small default value to prevent division by zero
        }

        /// <summary>
        /// Evaluate the Inverse Square Kernel at the given distance.
        /// </summary>
        /// <param name="distance">Distance from the center</param>
        /// <returns>Weight calculated by the kernel</returns>
        /// <exception cref="ArgumentException">Thrown when distance is negative, NaN, or Infinity.</exception>
        public double Evaluate(double distance)
        {
            if (distance < 0 || double.IsNaN(distance) || double.IsInfinity(distance))
            {
                throw new ArgumentException("Distance must be non-negative and finite.", nameof(distance));
            }

            return 1.0 / (distance * distance + _epsilon);
        }
    }
}