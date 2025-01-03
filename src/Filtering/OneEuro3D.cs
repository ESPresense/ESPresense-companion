using MathNet.Spatial.Euclidean;
using System;

public class OneEuroFilter3D
{
    private OneEuroFilter _filterX;
    private OneEuroFilter _filterY;
    private OneEuroFilter _filterZ;

    public OneEuroFilter3D(double minCutoff = 1.0, double beta = 0.0, double dCutoff = 1.0)
    {
        _filterX = new OneEuroFilter(minCutoff, beta, dCutoff);
        _filterY = new OneEuroFilter(minCutoff, beta, dCutoff);
        _filterZ = new OneEuroFilter(minCutoff, beta, dCutoff);
    }

    /// <summary>
    /// Smooths incoming 3D location with separate 1€ filters on each axis.
    /// </summary>
    public Point3D Filter(Point3D rawPoint, DateTime now)
    {
        double fx = _filterX.Filter(rawPoint.X, now);
        double fy = _filterY.Filter(rawPoint.Y, now);
        double fz = _filterZ.Filter(rawPoint.Z, now);
        return new Point3D(fx, fy, fz);
    }
}
