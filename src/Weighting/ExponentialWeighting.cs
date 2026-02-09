namespace ESPresense.Weighting;

/// <summary>
/// Exponential weighting that provides steep decay for later-ranked nodes.
/// Lambda parameter controls the decay rate (default: 1.0).
/// Lambda = 1.0 is equivalent to linear weighting.
/// Higher lambda values create steeper decay, emphasizing closer nodes more strongly.
/// </summary>
public class ExponentialWeighting : IWeighting
{
    private readonly double _lambda;

    public ExponentialWeighting(Dictionary<string, double>? props)
    {
        _lambda = props != null && props.TryGetValue("lambda", out var lambda) ? lambda : 1.0;
    }

    public double Get(int index, int total)
    {
        return Math.Pow((double)total - index, _lambda) / Math.Pow(total, _lambda);
    }
}