namespace ESPresense.Weighting;

public interface IKernel
{
    double Evaluate(double distance);
}