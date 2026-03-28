namespace ESPresense.Weighting;

/// <summary>
/// Represents a kernel function used for weighting measurements based on distance.
/// </summary>
public interface IKernel
{
    /// <summary>
    /// Evaluates the kernel at the given distance.
    /// </summary>
    /// <param name="distance">The distance from the kernel center.</param>
    /// <returns>The weight computed by the kernel.</returns>
    double Evaluate(double distance);
}