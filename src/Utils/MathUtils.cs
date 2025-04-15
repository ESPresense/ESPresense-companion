using System;
using System.Collections.Generic;
using System.Linq;

namespace ESPresense.Companion.Utils
{
    public static class MathUtils
    {
        /// <summary>
        /// Calculates the Pearson correlation coefficient between two lists of doubles.
        /// </summary>
        /// <param name="x">The first list of values.</param>
        /// <param name="y">The second list of values.</param>
        /// <returns>The Pearson correlation coefficient, or 0 if calculation is not possible.</returns>
        public static double CalculatePearsonCorrelation(List<double> x, List<double> y)
        {
            if (x == null || y == null || x.Count != y.Count || x.Count < 2)
                return 0;

            double sumX = 0;
            double sumY = 0;
            double sumXY = 0;
            double sumX2 = 0;
            double sumY2 = 0;
            int n = x.Count;

            for (int i = 0; i < n; i++)
            {
                sumX += x[i];
                sumY += y[i];
                sumXY += x[i] * y[i];
                sumX2 += x[i] * x[i];
                sumY2 += y[i] * y[i];
            }

            double numerator = n * sumXY - sumX * sumY;
            double denominator = Math.Sqrt((n * sumX2 - sumX * sumX) * (n * sumY2 - sumY * sumY));

            // Avoid division by zero if variance is zero
            return (denominator != 0) ? numerator / denominator : 0;
        }
    }
}