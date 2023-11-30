using System.Numerics;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Extensions
{
    public static class PointExtensions
    {
        public static Point2D ToPoint2D(this Point3D p) => new(p.X, p.Y);
        public static Point3D ToPoint3D(this Vector3 p) => new(p.X, p.Y, p.Z);
    }
}
