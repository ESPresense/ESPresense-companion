namespace ESPresense.Weighting;

public class EqualWeighting : IWeighting
{
    public double Get(int index, int total)
    {
        return 1.0 / total;
    }
}