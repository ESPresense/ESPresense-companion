using ESPresense.Locators;
using MathNet.Numerics.Optimization;
using MathNet.Spatial.Euclidean;
using Newtonsoft.Json;
using MathNet.Numerics.LinearAlgebra;

namespace ESPresense.Models
{
    public class Scenario(Config? config, ILocate locator, string? name)
    {
        const bool DisableFiltering = false;
        private Config? Config { get; } = config;
        private ILocate Locator { get; } = locator;

        public bool Current => DateTime.UtcNow - LastHit < TimeSpan.FromSeconds(Config?.Timeout ?? 30);
        public int? Confidence { get; set; }
        public double? Minimum { get; set; }
        [JsonIgnore] public Point3D LastLocation { get; set; }
        public Point3D Location { private set; get; }
        public double? Scale { get; set; }
        public int? Fixes { get; set; }
        public string? Name { get; } = name;
        public Room? Room { get; set; }
        public double? Error { get; set; }
        public int? Iterations { get; set; }
        public ExitCondition ReasonForExit { get; set; }
        public Floor? Floor { get; set; }
        public DateTime? LastHit { get; set; }
        public double Probability { get; set; } = 1.0;

        // 1€ filter for 3D
        // Tune minCutoff, beta, and dCutoff for your application:
        private OneEuroFilter3D _oneEuroFilter = new OneEuroFilter3D(minCutoff: 1.0, beta: 0.005, dCutoff: 1.0);

        public bool Locate()
        {
            return Locator.Locate(this);
        }

        public void UpdateLocation(Point3D newLocation)
        {
            // If we haven’t gotten any location yet, just set it
            if (Location == default || DisableFiltering)
            {
                Location = newLocation;
                LastHit = DateTime.UtcNow;
                return;
            }

            var now = DateTime.UtcNow;
            LastHit = now;

            // Smooth the new reading using the 3D 1€ filter
            var smoothedPoint = _oneEuroFilter.Filter(newLocation, now);

            // Store it
            Location = smoothedPoint;
        }
    }
}
