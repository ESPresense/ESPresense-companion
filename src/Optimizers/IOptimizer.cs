using ESPresense.Models;

namespace ESPresense.Optimizers;

public interface IOptimizer
{
    public string Name { get; }
    public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings);
}