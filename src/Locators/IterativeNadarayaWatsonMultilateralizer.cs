using ESPresense.Extensions;
using ESPresense.Models;
using ESPresense.Weighting;
using MathNet.Spatial.Euclidean;
using Serilog;

namespace ESPresense.Locators
{
    public class IterativeNadarayaWatsonMultilateralizer : ILocate
    {
        private readonly Device _device;
        private readonly Floor _floor;
        private readonly State _state;
        private readonly IKernel _kernel;

        // How many times to iterate the scale-fitting process
        private readonly int _maxIterations;

        public IterativeNadarayaWatsonMultilateralizer(
            Device device,
            Floor floor,
            State state,
            ConfigKernel? kernelConfig = null,
            int maxIterations = 2 // default to 2 iterations
        )
        {
            _device = device;
            _floor = floor;
            _state = state;
            _maxIterations = maxIterations;

            _kernel = kernelConfig?.Algorithm?.ToLower() switch
            {
                "epanechnikov" => new EpanechnikovKernel(kernelConfig?.Props),
                "gaussian"     => new GaussianKernel(kernelConfig?.Props),
                _              => new InverseSquareKernel(kernelConfig?.Props)
            };
        }

        public bool Locate(Scenario scenario)
        {
            // 1. Collect nodes relevant to this device & floor
            var nodes = _device.Nodes.Values
                .Where(a => a.Current && (a.Node?.Floors?.Contains(_floor) ?? false))
                .OrderBy(a => a.Distance)
                .ToArray();

            if (nodes.Length < 2)
            {
                // Not enough data to locate
                scenario.Room = null;
                scenario.Confidence = 0;
                scenario.Error = null;
                scenario.Floor = null;
                return false;
            }

            scenario.Floor = _floor;
            scenario.Minimum = nodes.Min(n => (double?)n.Distance);
            scenario.LastHit = nodes.Max(n => n.LastHit);
            scenario.Fixes   = nodes.Length;

            // 2. Initial guess: do a standard Nadaraya-Watson with alpha=1
            double alpha = 1.0;
            Point3D locationEstimate = ComputeNadarayaWatsonLocation(nodes, alpha);

            // 3. Iteratively refine alpha and location
            for (int i = 0; i < _maxIterations; i++)
            {
                // Compute new alpha by comparing geometric distance vs. measured distance
                double newAlpha = ComputeScaleFactor(locationEstimate, nodes);

                // Update location with newAlpha
                Point3D newLocation = ComputeNadarayaWatsonLocation(nodes, newAlpha);

                // Optional: check for convergence if desired (not strictly required)
                // if (Math.Abs(newAlpha - alpha) < 0.001 &&
                //     newLocation.DistanceTo(locationEstimate) < 0.01)
                // {
                //     // close enough, break early
                //     alpha = newAlpha;
                //     locationEstimate = newLocation;
                //     break;
                // }

                alpha = newAlpha;
                locationEstimate = newLocation;
            }

            // 4. Update scenario with final location + alpha
            scenario.UpdateLocation(locationEstimate);
            scenario.Scale = alpha;

            // 5. Optional: compute a weighted error just like before
            double totalWeight = 0.0;
            double weightedSumErrors = 0.0;
            foreach (var node in nodes)
            {
                double scaledDist = alpha * node.Distance;
                double w = _kernel.Evaluate(scaledDist);
                double residual = locationEstimate.DistanceTo(node.Node!.Location) - node.Distance;
                weightedSumErrors += w * Math.Pow(residual, 2);
                totalWeight += w;
            }

            double weightedError = (totalWeight > 0) ? (weightedSumErrors / totalWeight) : 0;
            scenario.Error = weightedError;

            scenario.Confidence = (int)Math.Clamp((((double)scenario.Fixes*3)) +( 100 - (Math.Pow(scenario.Error/10 ?? 1, 2) + Math.Pow(5*(1 - (scenario.Scale ?? 1)), 2))), 10, 100);
            if (_device.Id.Contains("android"))
                Log.Information("Scenario {Scenario} confidence {Confidence}% fixes {fixes:0} error {Error:00.0} scale {Scale:0.0}", scenario.Name, scenario.Confidence,scenario.Fixes, scenario.Error, scenario.Scale);
            // 7. Decide whether to accept or reject
            if (scenario.Confidence <= 0) return false;
            if (Math.Abs(scenario.Location.DistanceTo(scenario.LastLocation)) < 0.1) return false;

            // 8. If there's a polygon for rooms, figure out which room we're in
            scenario.Room = _floor.Rooms.Values.FirstOrDefault(a =>
                a.Polygon?.EnclosesPoint(scenario.Location.ToPoint2D()) ?? false);

            return true;
        }

        /// <summary>
        /// Basic Nadaraya-Watson calculation with a given alpha scale.
        /// Distances are multiplied by alpha before passing into the kernel.
        /// </summary>
        private Point3D ComputeNadarayaWatsonLocation(DeviceToNode[] nodes, double alpha)
        {
            double totalWeight = 0.0;
            double sumX = 0.0, sumY = 0.0, sumZ = 0.0;

            foreach (var nd in nodes)
            {
                double scaledDist = alpha * nd.Distance;
                double w = _kernel.Evaluate(scaledDist);
                sumX += nd.Node!.Location.X * w;
                sumY += nd.Node!.Location.Y * w;
                sumZ += nd.Node!.Location.Z * w;
                totalWeight += w;
            }

            if (totalWeight <= 0.0)
                return new Point3D(0, 0, 0);

            return new Point3D(sumX / totalWeight, sumY / totalWeight, sumZ / totalWeight);
        }

        /// <summary>
        /// Compute alpha by fitting measured distances (r_i) to geometric distances
        /// from the estimated location to each node (delta_i).
        /// alpha ~ sum( delta_i * r_i ) / sum( r_i^2 ).
        /// </summary>
        private double ComputeScaleFactor(Point3D locationEstimate, DeviceToNode[] nodes)
        {
            double sumDr = 0.0;
            double sumR2 = 0.0;

            foreach (var nd in nodes)
            {
                double measuredDist = nd.Distance; // the "raw" distance from RSSI
                double geometricDist = locationEstimate.DistanceTo(nd.Node!.Location);

                sumDr += (geometricDist * measuredDist);
                sumR2 += (measuredDist * measuredDist);
            }

            if (sumR2 > 0.0)
                return sumDr / sumR2;

            return 1.0; // fallback if all distances zero or something unexpected
        }
    }
}