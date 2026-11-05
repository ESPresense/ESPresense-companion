using ESPresense.Utils;
using NUnit.Framework;

namespace ESPresense.Companion.Tests.Utils;

[TestFixture]
public class MathUtilsTests
{
    // ---- CalculatePearsonCorrelation ----

    [Test]
    public void CalculatePearsonCorrelation_NormalCase_ReturnsCoefficient()
    {
        var x = new List<double> { 1, 2, 3, 4 };
        var direct = new List<double> { 2, 4, 6, 8 };
        var inverse = new List<double> { 8, 6, 4, 2 };

        Assert.That(MathUtils.CalculatePearsonCorrelation(x, direct), Is.EqualTo(1d).Within(1e-9));
        Assert.That(MathUtils.CalculatePearsonCorrelation(x, inverse), Is.EqualTo(-1d).Within(1e-9));
    }

    [Test]
    public void CalculatePearsonCorrelation_FewerThanTwoPairs_ReturnsZeroSentinel()
    {
        Assert.That(MathUtils.CalculatePearsonCorrelation(new List<double>(), new List<double>()), Is.EqualTo(0d));
        Assert.That(MathUtils.CalculatePearsonCorrelation(new List<double> { 1 }, new List<double> { 2 }), Is.EqualTo(0d));
        Assert.That(MathUtils.CalculatePearsonCorrelation(new List<double> { 1, 2 }, new List<double> { 2 }), Is.EqualTo(0d));
    }

    [Test]
    public void CalculatePearsonCorrelation_ZeroVariance_ReturnsZeroSentinel()
    {
        var constant = new List<double> { 1, 1, 1 };
        var varying = new List<double> { 1, 2, 3 };

        Assert.That(MathUtils.CalculatePearsonCorrelation(constant, varying), Is.EqualTo(0d));
        Assert.That(MathUtils.CalculatePearsonCorrelation(varying, constant), Is.EqualTo(0d));
    }

    [Test]
    public void CalculatePearsonCorrelation_NonFiniteInput_NeverReturnsNaN()
    {
        var withNaN = MathUtils.CalculatePearsonCorrelation(new List<double> { double.NaN, 1, 2 }, new List<double> { 1, 2, 3 });
        var withInfinity = MathUtils.CalculatePearsonCorrelation(new List<double> { double.PositiveInfinity, 1, 2 }, new List<double> { 1, 2, 3 });

        Assert.That(double.IsNaN(withNaN), Is.False);
        Assert.That(double.IsNaN(withInfinity), Is.False);
    }

    // ---- CalculateConfidence ----

    [Test]
    public void CalculateConfidence_NormalCase_CombinesCoverageAndQuality()
    {
        // coverage: 50 * 2/4 = 25; error 5 -> errScore 0.5; r 0.5 -> quality 50 * (0.6*0.5 + 0.4*0.5) = 25
        Assert.That(MathUtils.CalculateConfidence(5.0, 0.5, 2, 4), Is.EqualTo(50));
        // no error, perfect correlation, full coverage
        Assert.That(MathUtils.CalculateConfidence(0.0, 1.0, 4, 4), Is.EqualTo(100));
    }

    [Test]
    public void CalculateConfidence_NaNError_IsTreatedAsWorstCase()
    {
        var withNaN = MathUtils.CalculateConfidence(double.NaN, 1.0, 4, 4);
        var withNoError = MathUtils.CalculateConfidence(null, 1.0, 4, 4);

        Assert.That(withNaN, Is.EqualTo(withNoError));
        Assert.That(withNaN, Is.EqualTo(70)); // 50 coverage + 50 * 0.4 * 1.0
    }

    [Test]
    public void CalculateConfidence_InfiniteError_IsTreatedAsWorstCase()
    {
        var withNoError = MathUtils.CalculateConfidence(null, 1.0, 4, 4);

        Assert.That(MathUtils.CalculateConfidence(double.PositiveInfinity, 1.0, 4, 4), Is.EqualTo(withNoError));
        Assert.That(MathUtils.CalculateConfidence(double.NegativeInfinity, 1.0, 4, 4), Is.EqualTo(withNoError));
    }

    [Test]
    public void CalculateConfidence_NaNCorrelation_IsTreatedAsZero()
    {
        var withNaN = MathUtils.CalculateConfidence(0.0, double.NaN, 4, 4);
        var withZero = MathUtils.CalculateConfidence(0.0, 0.0, 4, 4);

        Assert.That(withNaN, Is.EqualTo(withZero));
        Assert.That(withNaN, Is.EqualTo(80)); // 50 coverage + 50 * 0.6 * 1.0
    }

    [Test]
    public void CalculateConfidence_InfiniteCorrelation_IsTreatedAsZero()
    {
        var withZero = MathUtils.CalculateConfidence(0.0, 0.0, 4, 4);

        Assert.That(MathUtils.CalculateConfidence(0.0, double.PositiveInfinity, 4, 4), Is.EqualTo(withZero));
        Assert.That(MathUtils.CalculateConfidence(0.0, double.NegativeInfinity, 4, 4), Is.EqualTo(withZero));
    }

    [Test]
    public void CalculateConfidence_AlwaysWithinFloorAndCeiling()
    {
        Assert.That(MathUtils.CalculateConfidence(double.NaN, double.NaN, 0, 0), Is.EqualTo(5));
        Assert.That(MathUtils.CalculateConfidence(double.PositiveInfinity, double.NegativeInfinity, 0, 0), Is.EqualTo(5));
        Assert.That(MathUtils.CalculateConfidence(0.0, 1.0, 10, 4), Is.EqualTo(100));
        Assert.That(MathUtils.CalculateConfidence(-100.0, 5.0, 4, 4), Is.InRange(5, 100));
    }
}
