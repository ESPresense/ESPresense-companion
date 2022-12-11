using MathNet.Spatial.Euclidean;

namespace ESPresense.Extensions
{
    public static class PointExtensions
    {
        public static Point2D ToPoint2D(this Point3D p) => new(p.X, p.Y);
    }
}
