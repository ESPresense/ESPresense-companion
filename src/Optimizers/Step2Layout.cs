namespace ESPresense.Optimizers;

/// <summary>
/// Defines the 4N blocked parameter-vector layout used in the Step-2 antenna-aware
/// absorption / pointing optimisation.
///
/// For N nodes the flat vector is laid out as four contiguous blocks:
///
///   [ abs_0 … abs_{N-1} | sinAz_0 … sinAz_{N-1} | cosAz_0 … cosAz_{N-1} | sinEl_0 … sinEl_{N-1} ]
///     ^--- block 0 ----^   ^------- block 1 ------^   ^------- block 2 ------^   ^-- block 3 --^
///
/// All downstream code that constructs, reads, or differentiates the Step-2 vector
/// MUST use the helpers on this class so that the mapping is a single source of truth.
/// </summary>
public static class Step2Layout
{
    // -----------------------------------------------------------------------
    // Block ordinals (zero-based block index)
    // -----------------------------------------------------------------------

    /// <summary>Block index for per-node path-loss absorption coefficients.</summary>
    public const int AbsBlock = 0;

    /// <summary>Block index for sin(azimuth) of each node's antenna boresight.</summary>
    public const int SinAzBlock = 1;

    /// <summary>Block index for cos(azimuth) of each node's antenna boresight.</summary>
    public const int CosAzBlock = 2;

    /// <summary>Block index for sin(elevation) of each node's antenna boresight.</summary>
    public const int SinElBlock = 3;

    /// <summary>Total number of parameter blocks.</summary>
    public const int BlockCount = 4;

    // -----------------------------------------------------------------------
    // Total vector length
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns the total length of the Step-2 parameter vector for <paramref name="nodeCount"/> nodes.
    /// </summary>
    public static int VectorLength(int nodeCount) => VectorLength(nodeCount, nodeCount);

    /// <summary>
    /// Returns the total length of the Step-2 parameter vector when absorption is optimized
    /// for <paramref name="absorptionCount"/> ids and pointing is optimized only for
    /// <paramref name="directionalCount"/> directional ids.
    /// </summary>
    public static int VectorLength(int absorptionCount, int directionalCount) => absorptionCount + (3 * directionalCount);

    // -----------------------------------------------------------------------
    // Block start offsets
    // -----------------------------------------------------------------------

    /// <summary>Index of the first element of the absorption block.</summary>
    public static int AbsOffset(int nodeCount) => 0;

    /// <summary>Index of the first element of the sinAz block.</summary>
    public static int SinAzOffset(int nodeCount) => nodeCount;

    /// <summary>Index of the first element of the sinAz block.</summary>
    public static int SinAzOffset(int absorptionCount, int directionalCount) => absorptionCount;

    /// <summary>Index of the first element of the cosAz block.</summary>
    public static int CosAzOffset(int nodeCount) => 2 * nodeCount;

    /// <summary>Index of the first element of the cosAz block.</summary>
    public static int CosAzOffset(int absorptionCount, int directionalCount) => absorptionCount + directionalCount;

    /// <summary>Index of the first element of the sinEl block.</summary>
    public static int SinElOffset(int nodeCount) => 3 * nodeCount;

    /// <summary>Index of the first element of the sinEl block.</summary>
    public static int SinElOffset(int absorptionCount, int directionalCount) => absorptionCount + (2 * directionalCount);

    // -----------------------------------------------------------------------
    // Per-node element indices
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns the flat-vector index for the absorption coefficient of node
    /// <paramref name="nodeIndex"/> within a vector sized for <paramref name="nodeCount"/> nodes.
    /// </summary>
    public static int AbsIndex(int nodeIndex, int nodeCount) => AbsOffset(nodeCount) + nodeIndex;

    /// <summary>
    /// Returns the flat-vector index for sin(azimuth) of node <paramref name="nodeIndex"/>.
    /// </summary>
    public static int SinAzIndex(int nodeIndex, int nodeCount) => SinAzOffset(nodeCount) + nodeIndex;

    /// <summary>
    /// Returns the flat-vector index for sin(azimuth) of directional node <paramref name="nodeIndex"/>.
    /// </summary>
    public static int SinAzIndex(int nodeIndex, int absorptionCount, int directionalCount) => SinAzOffset(absorptionCount, directionalCount) + nodeIndex;

    /// <summary>
    /// Returns the flat-vector index for cos(azimuth) of node <paramref name="nodeIndex"/>.
    /// </summary>
    public static int CosAzIndex(int nodeIndex, int nodeCount) => CosAzOffset(nodeCount) + nodeIndex;

    /// <summary>
    /// Returns the flat-vector index for cos(azimuth) of directional node <paramref name="nodeIndex"/>.
    /// </summary>
    public static int CosAzIndex(int nodeIndex, int absorptionCount, int directionalCount) => CosAzOffset(absorptionCount, directionalCount) + nodeIndex;

    /// <summary>
    /// Returns the flat-vector index for sin(elevation) of node <paramref name="nodeIndex"/>.
    /// </summary>
    public static int SinElIndex(int nodeIndex, int nodeCount) => SinElOffset(nodeCount) + nodeIndex;

    /// <summary>
    /// Returns the flat-vector index for sin(elevation) of directional node <paramref name="nodeIndex"/>.
    /// </summary>
    public static int SinElIndex(int nodeIndex, int absorptionCount, int directionalCount) => SinElOffset(absorptionCount, directionalCount) + nodeIndex;

    // -----------------------------------------------------------------------
    // Convenience: extract per-node values from a populated vector
    // -----------------------------------------------------------------------

    /// <summary>
    /// Decodes the reparameterised azimuth for node <paramref name="i"/> from the flat vector.
    /// Returns azimuth in radians via atan2(sinAz, cosAz).
    /// </summary>
    public static double GetAzimuthRad(IReadOnlyList<double> x, int i, int nodeCount)
    {
        return Math.Atan2(x[SinAzIndex(i, nodeCount)], x[CosAzIndex(i, nodeCount)]);
    }

    /// <summary>
    /// Decodes the reparameterised azimuth for directional node <paramref name="i"/> from the flat vector.
    /// Returns azimuth in radians via atan2(sinAz, cosAz).
    /// </summary>
    public static double GetAzimuthRad(IReadOnlyList<double> x, int i, int absorptionCount, int directionalCount)
    {
        return Math.Atan2(x[SinAzIndex(i, absorptionCount, directionalCount)], x[CosAzIndex(i, absorptionCount, directionalCount)]);
    }

    /// <summary>
    /// Decodes the reparameterised elevation for node <paramref name="i"/> from the flat vector.
    /// Returns elevation in radians via asin(clamp(sinEl, -1, 1)).
    /// </summary>
    public static double GetElevationRad(IReadOnlyList<double> x, int i, int nodeCount)
    {
        return Math.Asin(Math.Clamp(x[SinElIndex(i, nodeCount)], -1.0, 1.0));
    }

    /// <summary>
    /// Decodes the reparameterised elevation for directional node <paramref name="i"/> from the flat vector.
    /// Returns elevation in radians via asin(clamp(sinEl, -1, 1)).
    /// </summary>
    public static double GetElevationRad(IReadOnlyList<double> x, int i, int absorptionCount, int directionalCount)
    {
        return Math.Asin(Math.Clamp(x[SinElIndex(i, absorptionCount, directionalCount)], -1.0, 1.0));
    }

    /// <summary>
    /// Returns the unit-circle regularisation penalty for node <paramref name="i"/>:
    ///   0.1 * (sinAz² + cosAz² - 1)²
    /// This drives sinAz and cosAz toward the unit circle without enforcing a hard constraint.
    /// </summary>
    public static double UnitCirclePenalty(IReadOnlyList<double> x, int i, int nodeCount)
    {
        double sa = x[SinAzIndex(i, nodeCount)];
        double ca = x[CosAzIndex(i, nodeCount)];
        double dev = sa * sa + ca * ca - 1.0;
        return 0.1 * dev * dev;
    }

    /// <summary>
    /// Returns the unit-circle regularisation penalty for directional node <paramref name="i"/>.
    /// </summary>
    public static double UnitCirclePenalty(IReadOnlyList<double> x, int i, int absorptionCount, int directionalCount)
    {
        double sa = x[SinAzIndex(i, absorptionCount, directionalCount)];
        double ca = x[CosAzIndex(i, absorptionCount, directionalCount)];
        double dev = sa * sa + ca * ca - 1.0;
        return 0.1 * dev * dev;
    }

    // -----------------------------------------------------------------------
    // Convenience: build initial-guess slices
    // -----------------------------------------------------------------------

    /// <summary>
    /// Populates the absorption block of <paramref name="x"/> (assumed length
    /// <see cref="VectorLength"/>) from <paramref name="absorptions"/>.
    /// </summary>
    public static void SetAbsorptions(double[] x, IReadOnlyList<double> absorptions, int nodeCount)
    {
        for (int i = 0; i < nodeCount; i++)
            x[AbsIndex(i, nodeCount)] = absorptions[i];
    }

    /// <summary>
    /// Populates the sinAz and cosAz blocks from an array of azimuth angles in radians.
    /// </summary>
    public static void SetAzimuths(double[] x, IReadOnlyList<double> azimuthsRad, int nodeCount)
    {
        for (int i = 0; i < nodeCount; i++)
        {
            x[SinAzIndex(i, nodeCount)] = Math.Sin(azimuthsRad[i]);
            x[CosAzIndex(i, nodeCount)] = Math.Cos(azimuthsRad[i]);
        }
    }

    /// <summary>
    /// Populates the sinEl block from an array of elevation angles in radians.
    /// </summary>
    public static void SetElevations(double[] x, IReadOnlyList<double> elevationsRad, int nodeCount)
    {
        for (int i = 0; i < nodeCount; i++)
            x[SinElIndex(i, nodeCount)] = Math.Sin(elevationsRad[i]);
    }
}
