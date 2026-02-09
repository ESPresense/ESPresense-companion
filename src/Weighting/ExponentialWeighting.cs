namespace ESPresense.Weighting;

/// <summary>
/// Exponential weighting that provides moderate decay for later-ranked nodes.
/// Lambda parameter controls the decay rate (default: 1.5 for moderate-squared decay).
/// Higher lambda values create steeper decay, emphasizing closer nodes more strongly.
/// </summary>
public class ExponentialWeighting : IWeighting
{
    private readonly double _lambda;

    public ExponentialWeighting(Dictionary<string, double>? props)
    {
        _lambda = props != null && props.TryGetValue("lambda", out var lambda) ? lambda : 1.5;
    }

    public double Get(int index, int total)
    {
        return Math.Pow((double)total - index, _lambda) / Math.Pow(total, _lambda);
    }
}