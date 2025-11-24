using ESPresense.Utils;
using NUnit.Framework;

namespace ESPresense.Companion.Tests.Utils;

[TestFixture]
public class MathUtilsTests
{
    [Test]
    public void IsotonicRegression_AdjustsOutOfOrderValues()
    {
        double[] x = { 1d, 2d, 3d };
        double[] y = { 3d, 1d, 2d };

        var fitted = MathUtils.IsotonicRegression(x, y);

        Assert.That(fitted, Is.EqualTo(new[] { 2d, 2d, 2d }).Within(1e-6));
    }

    [Test]
    public void IsotonicRegression_DecreasingSequenceMaintainsOrder()
    {
        double[] x = { 1d, 2d, 3d, 4d };
        double[] y = { -50d, -55d, -54d, -70d };

        var fitted = MathUtils.IsotonicRegression(x, y, increasing: false);

        for (int i = 1; i < fitted.Length; i++)
        {
            Assert.That(fitted[i], Is.LessThanOrEqualTo(fitted[i - 1]).Within(1e-9));
        }
    }

    [Test]
    public void WeightedLinearRegression_ComputesSlopeAndIntercept()
    {
        double[] x = { 0d, 1d, 2d, 3d };
        double[] y = { 2d, 0d, -2d, -4d };

        var fit = MathUtils.WeightedLinearRegression(x, y);

        Assert.That(fit, Is.Not.Null);
        Assert.That(fit!.Value.Slope, Is.EqualTo(-2d).Within(1e-9));
        Assert.That(fit.Value.Intercept, Is.EqualTo(2d).Within(1e-9));
    }
}
