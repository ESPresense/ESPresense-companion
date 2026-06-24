using ESPresense.Models;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace ESPresense.Services;

/// <summary>
/// Continuously reconstructs a static RF-attenuation map per floor from the node-to-node links that
/// are already streaming in. Each link's "excess path loss beyond free space" is treated as a line
/// integral through an unknown attenuation field; many crisscrossing links let us invert for where
/// the attenuation actually sits (walls, appliances, the refrigerator). Runs online, no acquisition
/// step — the result is exposed for the calibration-page heatmap so a human can eyeball it ("that
/// rectangle is my fridge") and, later, fed into the locator to down-weight blocked paths.
/// </summary>
public class RadioTomographyService : BackgroundService
{
    private readonly State _state;

    // Tunables (kept conservative for a first cut).
    private const double FreeSpaceExponent = 2.0;  // n in rssi = ref - 10*n*log10(d)
    private const double CellSizeMeters = 1.0;
    private const int MaxCells = 1200;             // bound the inverse-problem size (Cholesky is O(cells^3))
    private const int MinLinksPerFloor = 6;
    private const double SmoothLambda = 0.4;        // spatial-smoothness strength, relative to data scale
    private const double RidgeEpsilon = 0.01;       // tiny magnitude prior: pulls truly-unobserved cells to 0
    private const double DecayDbPerCycle = 1.0;    // how fast the per-link "clean" peak forgets
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    // Per-link rolling "clean" (least-shadowed) RSSI, keyed "tx|rx". The decaying max strips out
    // transient people walking through a link while keeping the persistent static structure.
    private readonly Dictionary<string, double> _cleanRssi = new();

    public TomographyResult Latest { get; private set; } = new();

    public RadioTomographyService(State state)
    {
        _state = state;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Latest = Compute();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Radio tomography compute failed");
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }

    private TomographyResult Compute()
    {
        var result = new TomographyResult { Updated = DateTime.UtcNow };

        foreach (var floor in _state.Floors.Values)
        {
            if (floor.Bounds is not { Length: 2 }) continue;
            var tf = ComputeFloor(floor);
            if (tf != null) result.Floors.Add(tf);
        }

        return result;
    }

    private TomographyFloor? ComputeFloor(Floor floor)
    {
        double minX = Math.Min(floor.Bounds![0].X, floor.Bounds[1].X);
        double minY = Math.Min(floor.Bounds[0].Y, floor.Bounds[1].Y);
        double maxX = Math.Max(floor.Bounds[0].X, floor.Bounds[1].X);
        double maxY = Math.Max(floor.Bounds[0].Y, floor.Bounds[1].Y);
        double w = maxX - minX, h = maxY - minY;
        if (w <= 0 || h <= 0) return null;

        // Pick a cell size that keeps the grid under the size cap.
        double cell = CellSizeMeters;
        while (Math.Ceiling(w / cell) * Math.Ceiling(h / cell) > MaxCells) cell *= 1.5;
        int cols = (int)Math.Ceiling(w / cell);
        int rows = (int)Math.Ceiling(h / cell);
        int cellCount = cols * rows;

        bool OnFloor(Node n) => n.HasLocation && (n.Floors?.Any(f => f.Id == floor.Id) ?? false);

        // Gather links between nodes on this floor.
        var rows_W = new List<double[]>();
        var ys = new List<double>();
        var coverage = new double[cellCount];

        foreach (var tx in _state.Nodes.Values)
        {
            if (!OnFloor(tx)) continue;
            foreach (var meas in tx.RxNodes.Values)
            {
                var rx = meas.Rx;
                if (rx == null || !OnFloor(rx) || !meas.Current || meas.Rssi == 0) continue;

                double d = Math.Sqrt(Math.Pow(tx.Location.X - rx.Location.X, 2) + Math.Pow(tx.Location.Y - rx.Location.Y, 2));
                if (d < 0.5) continue;

                double clean = UpdateClean($"{tx.Id}|{rx.Id}", meas.Rssi);
                double freeExpected = meas.RefRssi - 10.0 * FreeSpaceExponent * Math.Log10(d);
                double excess = freeExpected - clean;     // dB of loss beyond free space
                if (excess < 0) excess = 0;               // can't be "less than free space" (ignore rare constructive multipath)

                var rowW = new double[cellCount];
                RasterizeRay(tx.Location.X, tx.Location.Y, rx.Location.X, rx.Location.Y, minX, minY, cell, cols, rows, rowW);
                double rayLen = rowW.Sum();
                if (rayLen <= 0) continue;

                for (int c = 0; c < cellCount; c++) coverage[c] += rowW[c];
                rows_W.Add(rowW);
                ys.Add(excess);
            }
        }

        if (rows_W.Count < MinLinksPerFloor) return null;

        var attenuation = SolveSmooth(rows_W, ys, cols, rows);

        var tf = new TomographyFloor
        {
            FloorId = floor.Id,
            FloorName = floor.Name,
            MinX = minX,
            MinY = minY,
            CellSize = cell,
            Cols = cols,
            Rows = rows,
            Attenuation = attenuation,
            Coverage = coverage,
            Links = rows_W.Count,
            MaxAttenuation = attenuation.Length > 0 ? attenuation.Max() : 0
        };
        return tf;
    }

    private double UpdateClean(string key, double rssi)
    {
        if (_cleanRssi.TryGetValue(key, out var prev))
        {
            double clean = Math.Max(rssi, prev - DecayDbPerCycle);
            _cleanRssi[key] = clean;
            return clean;
        }
        _cleanRssi[key] = rssi;
        return rssi;
    }

    /// <summary>Accumulate per-cell path length for the segment by fine sampling.</summary>
    private static void RasterizeRay(double x0, double y0, double x1, double y1,
        double minX, double minY, double cell, int cols, int rows, double[] rowW)
    {
        double dx = x1 - x0, dy = y1 - y0;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len <= 0) return;
        int steps = Math.Max(1, (int)Math.Ceiling(len / (cell * 0.25)));
        double stepLen = len / steps;
        for (int s = 0; s < steps; s++)
        {
            double t = (s + 0.5) / steps;
            double px = x0 + dx * t, py = y0 + dy * t;
            int col = (int)((px - minX) / cell);
            int row = (int)((py - minY) / cell);
            if (col < 0 || col >= cols || row < 0 || row >= rows) continue;
            rowW[row * cols + col] += stepLen;
        }
    }

    /// <summary>
    /// Smoothness-regularized non-negative reconstruction:
    ///   min ||Wx - y||^2 + λ·Σ_neighbours (x_i - x_j)^2 + ε||x||^2,  then clamp x >= 0.
    /// The Laplacian term couples adjacent cells so the field interpolates smoothly between links
    /// (no streaking, contiguous blobs) instead of treating every cell independently. The tiny ε
    /// ridge fixes the Laplacian's constant null space and pulls genuinely unobserved cells to 0.
    /// </summary>
    private static double[] SolveSmooth(List<double[]> rowsW, List<double> ys, int cols, int rows)
    {
        int cellCount = cols * rows;
        int L = rowsW.Count;
        var W = Matrix<double>.Build.Dense(L, cellCount, (i, j) => rowsW[i][j]);
        var y = Vector<double>.Build.Dense(L, i => ys[i]);

        var A = W.TransposeThisAndMultiply(W);            // C x C
        double meanDiag = 0;
        for (int i = 0; i < cellCount; i++) meanDiag += A[i, i];
        meanDiag = cellCount > 0 ? meanDiag / cellCount : 1.0;
        double lambda = SmoothLambda * meanDiag;
        double eps = RidgeEpsilon * meanDiag + 1e-9;

        // Add λ·(graph Laplacian over 4-neighbours): x^T L x = Σ_edges (x_i - x_j)^2.
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                int idx = r * cols + c;
                void Edge(int nr, int nc)
                {
                    if (nr < 0 || nr >= rows || nc < 0 || nc >= cols) return;
                    int nidx = nr * cols + nc;
                    A[idx, idx] += lambda;
                    A[idx, nidx] -= lambda;
                }
                Edge(r - 1, c);
                Edge(r + 1, c);
                Edge(r, c - 1);
                Edge(r, c + 1);
            }
        for (int i = 0; i < cellCount; i++) A[i, i] += eps;

        var wty = W.TransposeThisAndMultiply(y);
        var x = A.Cholesky().Solve(wty);

        var result = new double[cellCount];
        for (int i = 0; i < cellCount; i++) result[i] = Math.Max(0, x[i]);
        return result;
    }
}
