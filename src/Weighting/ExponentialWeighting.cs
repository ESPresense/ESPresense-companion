namespace ESPresense.Weighting;

/// <summary>
/// Exponential weighting that provides steep decay for later-ranked nodes.
/// Lambda parameter controls the decay rate (default: 3.0 for cubic decay).
/// Higher lambda values create steeper decay, emphasizing closer nodes more strongly.
/// </summary>
public class ExponentialWeighting : IWeighting
{
    private readonly double _lambda;

    public ExponentialWeighting(Dictionary<string, double>? props)
    {
        _lambda = props != null && props.TryGetValue("lambda", out var lambda) ? lambda : 3.0;
    }

    public double Get(int index, int total)
    {
        return Math.Pow((double)total - index, _lambda) / Math.Pow(total, _lambda);
    }
}