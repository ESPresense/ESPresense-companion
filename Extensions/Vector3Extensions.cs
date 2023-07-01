using System.Numerics;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Extensions;

public static class Vector3Extensions
{
    public static Vector3 ToVector3(this Point3D m) => new Vector3((float)m.X, (float)m.Y, (float)m.Z);
}