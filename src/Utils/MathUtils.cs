using System;
using System.Collections.Generic;
using System.Linq;

namespace ESPresense.Utils
{
    public static class MathUtils
    {
        // Constants for confidence calculation
        private const double MaxErr = 10.0;
        private const double AlphaErr = 0.60;
        private const double BetaR = 0.40;
        private const int ConfidenceFloor = 5;

        public static double CalculateRMSE(List<double> predicted, List<double> measured)
        {
            if (predicted == null || measured == null || predicted.Count != measured.Count || predicted.Count == 0)
                return double.NaN;

            double mse = predicted.Zip(measured, (p, m) => Math.Pow(p - m, 2)).Average();
            return Math.Sqrt(mse);
        }

        /// <summary>
        /// Calculates the Pearson correlation coefficient between two lists of doubles.
        /// </summary>
        /// <param name="x">The first list of values.</param>
        /// <param name="y">The second list of values.</param>
        /// <returns>The Pearson correlation coefficient, or 0 if calculation is not possible.</returns>
        public static double CalculatePearsonCorrelation(List<double> x, List<double> y)
        {
            if (x == null || y == null || x.Count != y.Count || x.Count < 2)
                return double.NaN;

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

        /// <summary>
        /// Calculate standardized confidence score based on error, correlation, and node coverage
        /// </summary>
        /// <param name="error">Error value (lower is better)</param>
        /// <param name="pearsonCorrelation">Pearson correlation coefficient (higher is better)</param>
        /// <param name="nodeCount">Number of nodes used in calculation</param>
        /// <param name="possibleNodeCount">Total number of possible nodes</param>
        /// <returns>Confidence value between ConfidenceFloor and 100</returns>
        public static int CalculateConfidence(double? error, double? pearsonCorrelation, int nodeCount, int possibleNodeCount)
        {
            // Ensure we have at least one node in possibleNodeCount to avoid division by zero
            if (possibleNodeCount <= 0) possibleNodeCount = 1;

            // Clamp nodeCount to ensure it doesn't exceed possibleNodeCount
            nodeCount = Math.Min(nodeCount, possibleNodeCount);

            // Calculate coverage component (0-50 points)
            double coveragePart = 50.0 * nodeCount / possibleNodeCount;

            // Calculate error score (0-1 range, 1 is best)
            double errScore = error.HasValue
                ? Math.Clamp(1.0 - (error.Value / MaxErr), 0.0, 1.0)
                : 0.0;

            // Calculate correlation score (0-1 range, 1 is best)
            double rScore = Math.Max(0.0, pearsonCorrelation ?? 0.0);

            // Calculate quality component (0-50 points)
            double qualityPart = 50.0 * (AlphaErr * errScore + BetaR * rScore);

            // Calculate final confidence score
            int confidence = (int)Math.Round(coveragePart + qualityPart);

            // Ensure confidence is within the defined floor and ceiling (100)
            return Math.Clamp(confidence, ConfidenceFloor, 100);
        }
    }
}