using System.Numerics;
using ESPresense.Models;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Optimizers;

/// <summary>
/// Places a device from per-node measured log10 distances by weighted least squares in log distance:
/// minimise sum_j w_j (log10 |p - node_j| - m_j)^2 over p. Built for the device_residual optimizer, which
/// solves every device window several times per cycle with candidate calibrations, so it trades the
/// locators' generality for speed and determinism:
///
/// A fixed grid (0.5 m cells over each floor's bounds at three heights) with the log10 distance from
/// every cell to every node computed once. A fit is a weighted sum of squared rows and an argmin, done
/// with SIMD. Leave-one-out fits (the position without node j) need no new sums: the loss without j is
/// the full loss minus j's term, so every node's LOO cell costs one more pass over the grid. The best cell
/// is then refined by a short pattern search, so positions are not quantised to the grid.
///
/// Independent of the locator classes so it is unit-testable and its answers depend only on its inputs.
/// </summary>
public sealed class DeviceGridSolver
{
    public const double DefaultCellSize = 0.5;

    /// <summary>Distances are floored here: a device is never closer to a node's antenna than this.</summary>
    public const double MinDistance = 0.3;

    /// <summary>Grids larger than this coarsen the cells rather than grow without bound.</summary>
    private const int MaxCells = 120_000;

    /// <summary>Log10 distance given to padding cells, so they never win an argmin.</summary>
    private const float PadLogD = 1000f;

    private static readonly float MinLog10Sq = (float)Math.Log10(MinDistance * MinDistance);

    private readonly float[] _nx, _ny, _nz;
    private readonly float[] _cx, _cy, _cz;
    private readonly float[][] _logD; // [node][cell], padded to a whole number of SIMD vectors
    private readonly Dictionary<string, int> _index;
    private readonly int _padded;

    public DeviceGridSolver(IReadOnlyList<(string Id, Point3D Location)> nodes, IReadOnlyList<(Point3D Min, Point3D Max)> floors, double cellSize = DefaultCellSize)
    {
        NodeIds = nodes.Select(n => n.Id).ToArray();
        _index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < NodeIds.Count; i++) _index.TryAdd(NodeIds[i], i);
        _nx = nodes.Select(n => (float)n.Location.X).ToArray();
        _ny = nodes.Select(n => (float)n.Location.Y).ToArray();
        _nz = nodes.Select(n => (float)n.Location.Z).ToArray();

        var volumes = floors
            .Select(f => (Min: new Point3D(Math.Min(f.Min.X, f.Max.X), Math.Min(f.Min.Y, f.Max.Y), Math.Min(f.Min.Z, f.Max.Z)),
                          Max: new Point3D(Math.Max(f.Min.X, f.Max.X), Math.Max(f.Min.Y, f.Max.Y), Math.Max(f.Min.Z, f.Max.Z))))
            .ToList();
        if (volumes.Count == 0 && nodes.Count > 0)
            volumes.Add((new Point3D(_nx.Min() - 1, _ny.Min() - 1, Math.Min(0, _nz.Min())),
                         new Point3D(_nx.Max() + 1, _ny.Max() + 1, Math.Max(_nz.Max(), 2.5))));

        // Coarsen if the floors are so large the grid would not fit comfortably in memory.
        CellSize = cellSize;
        while (CountCells(volumes, CellSize) > MaxCells) CellSize *= 1.25;

        var cells = new List<(float X, float Y, float Z)>();
        foreach (var (min, max) in volumes)
        {
            var zs = Heights(min.Z, max.Z);
            for (double x = min.X; x <= max.X + 1e-9; x += CellSize)
                for (double y = min.Y; y <= max.Y + 1e-9; y += CellSize)
                    foreach (var z in zs)
                        cells.Add(((float)x, (float)y, (float)z));
        }

        Cells = cells.Count;
        var vc = Vector<float>.Count;
        _padded = Math.Max(vc, (Cells + vc - 1) / vc * vc);
        _cx = new float[_padded];
        _cy = new float[_padded];
        _cz = new float[_padded];
        for (int c = 0; c < Cells; c++) (_cx[c], _cy[c], _cz[c]) = cells[c];

        _logD = new float[NodeIds.Count][];
        for (int n = 0; n < NodeIds.Count; n++)
        {
            var row = new float[_padded];
            for (int c = 0; c < _padded; c++)
                row[c] = c < Cells ? LogDistance(_cx[c], _cy[c], _cz[c], n) : PadLogD;
            _logD[n] = row;
        }
    }

    /// <summary>Node ids in index order.</summary>
    public IReadOnlyList<string> NodeIds { get; }

    public int Cells { get; }

    public double CellSize { get; }

    /// <summary>The node's index, or -1 when the solver has no position for it.</summary>
    public int IndexOf(string nodeId) => _index.TryGetValue(nodeId, out var i) ? i : -1;

    public Point3D NodeLocation(int node) => new(_nx[node], _ny[node], _nz[node]);

    /// <summary>
    /// The heights a floor is searched at: near the floor, the middle and below the ceiling, as the reference
    /// solver does; the refinement moves between them.
    /// </summary>
    private static double[] Heights(double lo, double hi)
    {
        if (hi - lo < 1.2) return new[] { (lo + hi) / 2 };
        return new[] { lo + 0.3, (lo + hi) / 2, hi - 0.6 };
    }

    private static long CountCells(IEnumerable<(Point3D Min, Point3D Max)> volumes, double cell) =>
        volumes.Sum(v => (long)(Math.Floor((v.Max.X - v.Min.X) / cell) + 1) * (long)(Math.Floor((v.Max.Y - v.Min.Y) / cell) + 1) * Heights(v.Min.Z, v.Max.Z).Length);

    private float LogDistance(float x, float y, float z, int node)
    {
        float dx = x - _nx[node], dy = y - _ny[node], dz = z - _nz[node];
        return 0.5f * MathF.Max(MathF.Log10(dx * dx + dy * dy + dz * dz), MinLog10Sq);
    }

    private static readonly object CacheLock = new();
    private static readonly List<(string Signature, DeviceGridSolver Solver)> Cache = new();

    /// <summary>A solver for <paramref name="geometry"/>, reused while the node positions and floors are unchanged.</summary>
    public static DeviceGridSolver For(DeviceGeometry geometry)
    {
        lock (CacheLock)
        {
            var hit = Cache.FindIndex(c => c.Signature == geometry.Signature);
            if (hit >= 0) return Cache[hit].Solver;
        }

        var nodes = geometry.Nodes.OrderBy(n => n.Key, StringComparer.OrdinalIgnoreCase).Select(n => (n.Key, n.Value)).ToList();
        var solver = new DeviceGridSolver(nodes, geometry.Floors);
        lock (CacheLock)
        {
            Cache.Add((geometry.Signature, solver));
            if (Cache.Count > 4) Cache.RemoveAt(0);
        }
        return solver;
    }

    /// <summary>Per-thread working memory for <see cref="Fit"/>.</summary>
    public sealed class Scratch
    {
        internal Scratch(int cells) => Loss = new float[cells];
        internal readonly float[] Loss;
    }

    public Scratch CreateScratch() => new(_padded);

    /// <summary>
    /// Fits one window. <paramref name="nodes"/> are node indices, <paramref name="m"/> the measured log10
    /// distance to each and <paramref name="w"/> its weight. Writes the log10 distance from the full fit to
    /// each node into <paramref name="fullLogD"/>, and, when <paramref name="looLogD"/> is not empty, the
    /// log10 distance to node j from the fit made without node j into <paramref name="looLogD"/>[j].
    /// Returns the full fit's position.
    /// </summary>
    public Point3D Fit(ReadOnlySpan<int> nodes, ReadOnlySpan<double> m, ReadOnlySpan<double> w,
        Span<double> fullLogD, Span<double> looLogD, Scratch scratch)
    {
        var loss = scratch.Loss;
        Array.Clear(loss);
        for (int j = 0; j < nodes.Length; j++)
            Accumulate(loss, _logD[nodes[j]], (float)m[j], (float)w[j]);

        var full = Refine(ArgMin(loss, null, 0, 0), nodes, m, w, skip: -1);
        for (int j = 0; j < nodes.Length; j++)
            fullLogD[j] = LogDistance(full.X, full.Y, full.Z, nodes[j]);

        if (!looLogD.IsEmpty)
        {
            for (int j = 0; j < nodes.Length; j++)
            {
                var cell = ArgMin(loss, _logD[nodes[j]], (float)m[j], (float)w[j]);
                var p = Refine(cell, nodes, m, w, skip: j);
                looLogD[j] = LogDistance(p.X, p.Y, p.Z, nodes[j]);
            }
        }

        return new Point3D(full.X, full.Y, full.Z);
    }

    /// <summary>loss[c] += w (row[c] - m)^2 for every cell.</summary>
    private static void Accumulate(float[] loss, float[] row, float m, float w)
    {
        int vc = Vector<float>.Count;
        var mv = new Vector<float>(m);
        var wv = new Vector<float>(w);
        for (int c = 0; c < loss.Length; c += vc)
        {
            var d = new Vector<float>(row, c) - mv;
            (new Vector<float>(loss, c) + wv * d * d).CopyTo(loss, c);
        }
    }

    /// <summary>The cell minimising loss[c] - w (row[c] - m)^2 (just loss[c] when <paramref name="row"/> is null).</summary>
    private static int ArgMin(float[] loss, float[]? row, float m, float w)
    {
        int vc = Vector<float>.Count;
        var best = new Vector<float>(float.MaxValue);
        var bestIndex = new Vector<int>(-1);
        Span<int> lanes = stackalloc int[vc];
        for (int i = 0; i < vc; i++) lanes[i] = i;
        var index = new Vector<int>(lanes);
        var step = new Vector<int>(vc);
        var mv = new Vector<float>(m);
        var wv = new Vector<float>(w);

        for (int c = 0; c < loss.Length; c += vc)
        {
            var v = new Vector<float>(loss, c);
            if (row != null)
            {
                var d = new Vector<float>(row, c) - mv;
                v -= wv * d * d;
            }
            var lower = Vector.LessThan(v, best);
            best = Vector.ConditionalSelect(lower, v, best);
            bestIndex = Vector.ConditionalSelect(lower, index, bestIndex);
            index += step;
        }

        float bv = float.MaxValue;
        int bi = 0;
        for (int i = 0; i < vc; i++)
            if (best[i] < bv || (best[i] == bv && bestIndex[i] < bi))
            {
                bv = best[i];
                bi = bestIndex[i];
            }
        return Math.Max(bi, 0);
    }

    private float Loss(float x, float y, float z, ReadOnlySpan<int> nodes, ReadOnlySpan<double> m, ReadOnlySpan<double> w, int skip)
    {
        float sum = 0;
        for (int j = 0; j < nodes.Length; j++)
        {
            if (j == skip) continue;
            var e = LogDistance(x, y, z, nodes[j]) - (float)m[j];
            sum += (float)w[j] * e * e;
        }
        return sum;
    }

    /// <summary>
    /// Pattern search from a grid cell: try a step along each axis both ways, take any that lowers the loss,
    /// and halve the step once none does. Moves at most about a cell and a half in each axis.
    /// </summary>
    private (float X, float Y, float Z) Refine(int cell, ReadOnlySpan<int> nodes, ReadOnlySpan<double> m, ReadOnlySpan<double> w, int skip)
    {
        float x = _cx[cell], y = _cy[cell], z = _cz[cell];
        var best = Loss(x, y, z, nodes, m, w, skip);
        var step = (float)CellSize / 2;
        for (int level = 0; level < 2; level++, step /= 2)
        {
            for (int iter = 0; iter < 4; iter++)
            {
                var moved = false;
                for (int axis = 0; axis < 3; axis++)
                    for (int sign = -1; sign <= 1; sign += 2)
                    {
                        float tx = x, ty = y, tz = z;
                        if (axis == 0) tx += sign * step;
                        else if (axis == 1) ty += sign * step;
                        else tz += sign * step;
                        var f = Loss(tx, ty, tz, nodes, m, w, skip);
                        if (f < best)
                        {
                            best = f;
                            (x, y, z) = (tx, ty, tz);
                            moved = true;
                        }
                    }
                if (!moved) break;
            }
        }
        return (x, y, z);
    }
}
