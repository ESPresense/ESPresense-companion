namespace ESPresense.Weighting;

using ESPresense.Models;

/// <summary>
/// Factory for creating IWeighting instances from configuration
/// </summary>
public static class WeightingFactory
{
    /// <summary>
    /// Creates an IWeighting instance from ConfigWeighting, defaulting to Gaussian if not specified
    /// </summary>
    public static IWeighting Create(ConfigWeighting? config)
    {
        return config?.Algorithm switch
        {
            "equal" => new EqualWeighting(),
            "linear" => new LinearWeighting(config.Props),
            "gaussian" => new GaussianWeighting(config.Props),
            "exponential" => new ExponentialWeighting(config.Props),
            _ => new GaussianWeighting(config.Props)
        };
    }
}
