using System.Numerics;

namespace ESPresense.Extensions;

public static class Matrix4x4Extensions
{
    public static Matrix4x4 Inverse(this Matrix4x4 m)
    {
        float a = m.M11, b = m.M12, c = m.M13, d = m.M14;
        float e = m.M21, f = m.M22, g = m.M23, h = m.M24;
        float i = m.M31, j = m.M32, k = m.M33, l = m.M34;
        float m0 = m.M41, n = m.M42, o = m.M43, p = m.M44;

        float q = a * f * k * p + b * g * l * m0 + c * h * i * n + d * e * j * o - a * g * l * n - b * h * k * m0 - c * e * j * p - d * f * i * o;
        float invDet = 1.0f / q;

        Matrix4x4 result;
        result.M11 = (f * k * p + g * l * n + h * j * o - f * l * o - g * j * p - h * k * n) * invDet;
        result.M12 = (b * l * o + c * j * p + d * k * n - b * k * p - c * l * n - d * j * o) * invDet;
        result.M13 = (b * g * p + c * h * n + d * f * o - b * h * o - c * g * p - d * f * n) * invDet;
        result.M14 = (b * h * k + c * g * j + d * f * l - b * g * l - c * h * j - d * f * k) * invDet;
        result.M21 = (e * l * o + g * i * p + h * k * m0 - e * k * p - g * l * m0 - h * i * o) * invDet;
        result.M22 = (a * k * p + c * l * m0 + d * i * o - a * l * o - c * i * p - d * k * m0) * invDet;
        result.M23 = (a * h * o + c * g * p + d * e * n - a * g * p - c * h * n - d * e * o) * invDet;
        result.M24 = (a * g * l + c * h * j + d * e * k - a * h * k - c * g * j - d * e * l) * invDet;
        result.M31 = (e * j * p + f * l * m0 + h * i * n - e * l * n - f * i * p - h * j * m0) * invDet;
        result.M32 = (a * l * n + b * i * p + d * j * m0 - a * j * p - b * l * m0 - d * i * n) * invDet;
        result.M33 = (a * g * p + b * h * m0 + d * e * o - a * h * o - b * g * p - d * e * n) * invDet;
        result.M34 = (a * h * j + b * g * i + d * e * k - a * g * k - b * h * i - d * e * j) * invDet;
        result.M41 = (e * k * n + f * i * o + g * j * m0 - e * j * o - f * k * m0 - g * i * n) * invDet;
        result.M42 = (a * j * o + b * k * m0 + c * i * n - a * k * n - b * i * o - c * j * m0) * invDet;
        result.M43 = (a * f * n + b * e * o + c * g * m0 - a * g * m0 - b * f * o - c * e * n) * invDet;
        result.M44 = (a * g * j + b * f * i + c * e * k - a * f * k - b * g * i - c * e * j) * invDet;

        return result;
    }

}