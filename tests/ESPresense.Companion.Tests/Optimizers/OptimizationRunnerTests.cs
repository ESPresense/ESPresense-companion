using ESPresense.Models;
using ESPresense.Optimizers;

namespace ESPresense.Companion.Tests.Optimizers;

public class OptimizationRunnerTests
{
    [Test]
    public void HasExcessiveAbsorptionRailing_RejectsPolarizedCandidate()
    {
        var optimization = new ConfigOptimization
        {
            Limits = new Dictionary<string, double>
            {
                ["absorption_min"] = 2.5,
                ["absorption_max"] = 5.0
            }
        };
        var results = new OptimizationResults
        {
            Nodes = new Dictionary<string, ProposedValues>
            {
                ["a"] = new() { Absorption = 2.5 },
                ["b"] = new() { Absorption = 3.2 },
                ["c"] = new() { Absorption = 3.8 },
                ["d"] = new() { Absorption = 4.1 },
                ["e"] = new() { Absorption = 5.0 }
            }
        };

        Assert.That(OptimizationRunner.HasExcessiveAbsorptionRailing(results, optimization), Is.True);

        results.Nodes["a"].Absorption = 2.6;
        Assert.That(OptimizationRunner.HasExcessiveAbsorptionRailing(results, optimization), Is.False);
    }
}
