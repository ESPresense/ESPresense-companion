using ESPresense.Optimizers;

namespace ESPresense.Companion.Tests.Optimizers;

[TestFixture]
public class Step2LayoutTests
{
    // -----------------------------------------------------------------------
    // VectorLength
    // -----------------------------------------------------------------------

    [Test]
    [TestCase(1, 4)]
    [TestCase(3, 12)]
    [TestCase(5, 20)]
    public void VectorLength_Returns4TimesNodeCount(int nodeCount, int expected)
    {
        Assert.That(Step2Layout.VectorLength(nodeCount), Is.EqualTo(expected));
    }

    // -----------------------------------------------------------------------
    // Block offsets
    // -----------------------------------------------------------------------

    [Test]
    public void AbsOffset_ReturnsZero()
    {
        Assert.That(Step2Layout.AbsOffset(4), Is.EqualTo(0));
    }

    [Test]
    public void SinAzOffset_ReturnsNodeCount()
    {
        const int n = 4;
        Assert.That(Step2Layout.SinAzOffset(n), Is.EqualTo(n));
    }

    [Test]
    public void CosAzOffset_Returns2TimesNodeCount()
    {
        const int n = 4;
        Assert.That(Step2Layout.CosAzOffset(n), Is.EqualTo(2 * n));
    }

    [Test]
    public void SinElOffset_Returns3TimesNodeCount()
    {
        const int n = 4;
        Assert.That(Step2Layout.SinElOffset(n), Is.EqualTo(3 * n));
    }

    // -----------------------------------------------------------------------
    // Per-node indices
    // -----------------------------------------------------------------------

    [Test]
    public void AbsIndex_IsNodeIndexWithinBlock0()
    {
        const int n = 5;
        for (int i = 0; i < n; i++)
            Assert.That(Step2Layout.AbsIndex(i, n), Is.EqualTo(i));
    }

    [Test]
    public void SinAzIndex_IsNodeIndexWithinBlock1()
    {
        const int n = 5;
        for (int i = 0; i < n; i++)
            Assert.That(Step2Layout.SinAzIndex(i, n), Is.EqualTo(n + i));
    }

    [Test]
    public void CosAzIndex_IsNodeIndexWithinBlock2()
    {
        const int n = 5;
        for (int i = 0; i < n; i++)
            Assert.That(Step2Layout.CosAzIndex(i, n), Is.EqualTo(2 * n + i));
    }

    [Test]
    public void SinElIndex_IsNodeIndexWithinBlock3()
    {
        const int n = 5;
        for (int i = 0; i < n; i++)
            Assert.That(Step2Layout.SinElIndex(i, n), Is.EqualTo(3 * n + i));
    }

    [Test]
    public void AllIndices_AreUniqueAndCoverFullVector()
    {
        const int n = 3;
        var indices = new HashSet<int>();
        for (int i = 0; i < n; i++)
        {
            indices.Add(Step2Layout.AbsIndex(i, n));
            indices.Add(Step2Layout.SinAzIndex(i, n));
            indices.Add(Step2Layout.CosAzIndex(i, n));
            indices.Add(Step2Layout.SinElIndex(i, n));
        }
        // Expect exactly VectorLength unique indices
        Assert.That(indices.Count, Is.EqualTo(Step2Layout.VectorLength(n)));
    }

    // -----------------------------------------------------------------------
    // SetAbsorptions / SetAzimuths / SetElevations round-trip
    // -----------------------------------------------------------------------

    [Test]
    public void SetAbsorptions_PopulatesBlock0Correctly()
    {
        const int n = 3;
        var x = new double[Step2Layout.VectorLength(n)];
        double[] absorptions = [2.0, 3.5, 2.8];

        Step2Layout.SetAbsorptions(x, absorptions, n);

        for (int i = 0; i < n; i++)
            Assert.That(x[Step2Layout.AbsIndex(i, n)], Is.EqualTo(absorptions[i]));
    }

    [Test]
    public void SetAzimuths_PopulatesSinAzAndCosAzBlocks()
    {
        const int n = 2;
        var x = new double[Step2Layout.VectorLength(n)];
        double[] azimuths = [0.0, Math.PI / 4];

        Step2Layout.SetAzimuths(x, azimuths, n);

        for (int i = 0; i < n; i++)
        {
            Assert.That(x[Step2Layout.SinAzIndex(i, n)], Is.EqualTo(Math.Sin(azimuths[i])).Within(1e-12));
            Assert.That(x[Step2Layout.CosAzIndex(i, n)], Is.EqualTo(Math.Cos(azimuths[i])).Within(1e-12));
        }
    }

    [Test]
    public void SetElevations_PopulatesSinElBlock()
    {
        const int n = 2;
        var x = new double[Step2Layout.VectorLength(n)];
        double[] elevations = [Math.PI / 2, Math.PI / 6];

        Step2Layout.SetElevations(x, elevations, n);

        for (int i = 0; i < n; i++)
            Assert.That(x[Step2Layout.SinElIndex(i, n)], Is.EqualTo(Math.Sin(elevations[i])).Within(1e-12));
    }

    // -----------------------------------------------------------------------
    // GetAzimuthRad / GetElevationRad round-trips
    // -----------------------------------------------------------------------

    [Test]
    [TestCase(0.0)]
    [TestCase(Math.PI / 4)]
    [TestCase(-Math.PI / 2)]
    [TestCase(Math.PI)]
    public void GetAzimuthRad_RoundTripsViaAtan2(double azimuthRad)
    {
        const int n = 1;
        var x = new double[Step2Layout.VectorLength(n)];
        Step2Layout.SetAzimuths(x, [azimuthRad], n);

        double result = Step2Layout.GetAzimuthRad(x, 0, n);
        Assert.That(result, Is.EqualTo(azimuthRad).Within(1e-12));
    }

    [Test]
    [TestCase(0.0)]
    [TestCase(Math.PI / 6)]
    [TestCase(Math.PI / 2)]
    [TestCase(-Math.PI / 3)]
    public void GetElevationRad_RoundTripsViaAsin(double elevationRad)
    {
        const int n = 1;
        var x = new double[Step2Layout.VectorLength(n)];
        Step2Layout.SetElevations(x, [elevationRad], n);

        double result = Step2Layout.GetElevationRad(x, 0, n);
        Assert.That(result, Is.EqualTo(elevationRad).Within(1e-12));
    }

    // -----------------------------------------------------------------------
    // UnitCirclePenalty
    // -----------------------------------------------------------------------

    [Test]
    public void UnitCirclePenalty_IsZeroWhenOnUnitCircle()
    {
        const int n = 1;
        var x = new double[Step2Layout.VectorLength(n)];
        // Set azimuth=0 → sinAz=0, cosAz=1 → exactly on unit circle
        Step2Layout.SetAzimuths(x, [0.0], n);

        double penalty = Step2Layout.UnitCirclePenalty(x, 0, n);
        Assert.That(penalty, Is.EqualTo(0.0).Within(1e-24));
    }

    [Test]
    public void UnitCirclePenalty_IsPositiveWhenOffUnitCircle()
    {
        const int n = 1;
        var x = new double[Step2Layout.VectorLength(n)];
        // Both sinAz and cosAz set to 1 → sinAz²+cosAz²=2 ≠ 1
        x[Step2Layout.SinAzIndex(0, n)] = 1.0;
        x[Step2Layout.CosAzIndex(0, n)] = 1.0;

        double penalty = Step2Layout.UnitCirclePenalty(x, 0, n);
        // dev = 2 - 1 = 1; penalty = 0.1 * 1^2 = 0.1
        Assert.That(penalty, Is.EqualTo(0.1).Within(1e-12));
    }

    [Test]
    public void UnitCirclePenalty_UsesLambdaOf0Point1()
    {
        const int n = 1;
        var x = new double[Step2Layout.VectorLength(n)];
        // sinAz=0, cosAz=0 → sinAz²+cosAz² = 0 → dev = -1
        // penalty = 0.1 * (-1)^2 = 0.1
        x[Step2Layout.SinAzIndex(0, n)] = 0.0;
        x[Step2Layout.CosAzIndex(0, n)] = 0.0;

        double penalty = Step2Layout.UnitCirclePenalty(x, 0, n);
        Assert.That(penalty, Is.EqualTo(0.1).Within(1e-12));
    }

    // -----------------------------------------------------------------------
    // Block constants
    // -----------------------------------------------------------------------

    [Test]
    public void BlockConstants_AreCorrect()
    {
        Assert.That(Step2Layout.AbsBlock, Is.EqualTo(0));
        Assert.That(Step2Layout.SinAzBlock, Is.EqualTo(1));
        Assert.That(Step2Layout.CosAzBlock, Is.EqualTo(2));
        Assert.That(Step2Layout.SinElBlock, Is.EqualTo(3));
        Assert.That(Step2Layout.BlockCount, Is.EqualTo(4));
    }
}
