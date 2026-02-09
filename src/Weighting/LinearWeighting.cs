namespace ESPresense.Weighting;

/// <summary>
/// Linear weighting that decreases weight proportionally with node distance rank.
/// Provides moderate emphasis on closer nodes with a linear dropoff.
/// For N nodes, weights are: N/N, (N-1)/N, (N-2)/N, ..., 1/N
/// </summary>
public class LinearWeighting : IWeighting
{
    public LinearWeighting(Dictionary<string, double>? props)
    {
    }

    public double Get(int index, int total)
    {
        return (double)(total - index) / total;
    }
}
