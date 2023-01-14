using ESPresense.Locators;
using MathNet.Numerics.Optimization;
using MathNet.Spatial.Euclidean;
using Newtonsoft.Json;

namespace ESPresense.Models
{
    public class Scenario
    {
        private readonly ILocate _locator;

        public Scenario(ILocate locator, string? name)
        {
            _locator = locator;
            Name = name;
        }

        public int? Confidence { get; set; }
        [JsonIgnore] public Point3D LastLocation { get; set; }
        public Point3D Location { get; set; }
        public double Scale { get; set; }
        public int? Fixes { get; set; }
        public string? Name { get; }
        public Room? Room { get; set; }
        public double? Error { get; set; }
        public int? Iterations { get; set; }
        public ExitCondition ReasonForExit { get; set; }
        public Floor? Floor { get; set; }

        public bool Locate()
        {
            return _locator.Locate(this);
        }
    }
}
