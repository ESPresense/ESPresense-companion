namespace ESPresense.Locators;

public class GaussianWeighting : IWeighting
{
    private readonly double _sigma;

    public GaussianWeighting(Dictionary<string, double>? props)
    {
        _sigma = props != null && props.TryGetValue("sigma", out var sigma) ? sigma : 0.3;
    }

    public double Get(int index, int total)
    {
        var x = (double)index / (total - 1);
        var y = 1d / Math.Sqrt(_sigma * 2d * Math.PI) * Math.Exp(-(Math.Pow(x, 2d) / (2d * Math.Pow(_sigma, 2d))));
        return y;
    }
}