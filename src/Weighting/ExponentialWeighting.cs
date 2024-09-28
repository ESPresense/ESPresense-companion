namespace ESPresense.Weighting;

public class ExponentialWeighting : IWeighting
{
    public ExponentialWeighting(Dictionary<string, double>? props)
    {
    }

    public double Get(int index, int total)
    {
        return Math.Pow((double)total - index, 3) / Math.Pow(total, 3);
    }
}