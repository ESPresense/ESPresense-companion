using System.Diagnostics;
using ESPresense.Optimizers;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Companion.Tests.Optimizers;

public class DeviceGridSolverTests
{
    /// <summary>Twelve nodes over two 16 x 12 m floors, at plug and furniture height.</summary>
    internal static readonly (string Id, double X, double Y, double Z)[] House =
    {
        ("n01", 0.5, 0.5, 0.4), ("n02", 8.0, 0.4, 1.0), ("n03", 15.5, 0.6, 0.5), ("n04", 0.4, 11.5, 1.2),
        ("n05", 8.2, 11.6, 0.4), ("n06", 15.6, 11.4, 0.9), ("n07", 6.0, 6.0, 0.5),
        ("n08", 1.0, 1.0, 3.5), ("n09", 15.0, 1.2, 4.0), ("n10", 1.2, 11.0, 3.4), ("n11", 14.8, 10.8, 4.2), ("n12", 9.0, 6.0, 3.6),
    };

    internal static readonly (Point3D Min, Point3D Max)[] Floors =
    {
        (new Point3D(0, 0, 0), new Point3D(16, 12, 3)),
        (new Point3D(0, 0, 3), new Point3D(16, 12, 6)),
    };

    internal static DeviceGridSolver Solver() => new(House.Select(n => (n.Id, new Point3D(n.X, n.Y, n.Z))).ToList(), Floors);

    private static double Log10Distance(Point3D a, Point3D b) => Math.Log10(Math.Max(a.DistanceTo(b), DeviceGridSolver.MinDistance));

    [TestCase(3.3, 4.7, 1.0)]
    [TestCase(12.1, 8.2, 0.8)]
    [TestCase(5.2, 9.9, 4.1)]
    public void ExactDistances_RecoverThePosition(double x, double y, double z)
    {
        var solver = Solver();
        var truth = new Point3D(x, y, z);
        var nodes = Enumerable.Range(0, House.Length).ToArray();
        var m = nodes.Select(n => Log10Distance(truth, solver.NodeLocation(n))).ToArray();
        var w = nodes.Select(_ => 1.0).ToArray();
        var full = new double[nodes.Length];
        var loo = new double[nodes.Length];

        var p = solver.Fit(nodes, m, w, full, loo, solver.CreateScratch());

        Assert.That(p.DistanceTo(truth), Is.LessThan(0.15), $"fitted {p}");
        for (int j = 0; j < nodes.Length; j++)
        {
            Assert.That(full[j], Is.EqualTo(m[j]).Within(0.03), $"node {j} full");
            Assert.That(loo[j], Is.EqualTo(m[j]).Within(0.03), $"node {j} leave-one-out");
        }
    }

    [Test]
    public void LeaveOneOut_DoesNotLetTheNodeJudgedPullTheFit()
    {
        // One node's measured distance is half the truth (it hears 8 dB hot at absorption 2.7). The full fit
        // is dragged toward it; the fit without it is not, so its leave-one-out residual shows the whole bias.
        var solver = Solver();
        var truth = new Point3D(6.5, 4.0, 1.0);
        var nodes = Enumerable.Range(0, 7).ToArray(); // the first floor's nodes
        var m = nodes.Select(n => Log10Distance(truth, solver.NodeLocation(n))).ToArray();
        m[6] -= Math.Log10(2);
        var w = nodes.Select(_ => 1.0).ToArray();
        var full = new double[nodes.Length];
        var loo = new double[nodes.Length];

        solver.Fit(nodes, m, w, full, loo, solver.CreateScratch());

        var fullResidual = full[6] - m[6];
        var looResidual = loo[6] - m[6];
        Assert.That(looResidual, Is.EqualTo(Math.Log10(2)).Within(0.03));
        Assert.That(fullResidual, Is.LessThan(looResidual - 0.02), "the full fit absorbs part of the bias");
    }

    [Test]
    public void ZeroWeight_LeavesTheNodeOut()
    {
        var solver = Solver();
        var truth = new Point3D(10.0, 3.0, 1.0);
        var nodes = Enumerable.Range(0, House.Length).ToArray();
        var m = nodes.Select(n => Log10Distance(truth, solver.NodeLocation(n))).ToArray();
        m[2] += 0.5; // wildly wrong
        var w = nodes.Select(n => n == 2 ? 0.0 : 1.0).ToArray();

        var p = solver.Fit(nodes, m, w, new double[nodes.Length], Span<double>.Empty, solver.CreateScratch());

        Assert.That(p.DistanceTo(truth), Is.LessThan(0.15));
    }

    [Test, Explicit("timing")]
    public void Timing()
    {
        var solver = Solver();
        var rnd = new Random(3);
        var nodes = Enumerable.Range(0, House.Length).ToArray();
        var w = nodes.Select(_ => 1.0).ToArray();
        var scratch = solver.CreateScratch();
        var full = new double[nodes.Length];
        var loo = new double[nodes.Length];
        const int n = 4000;
        var ms = Enumerable.Range(0, n).Select(_ =>
        {
            var truth = new Point3D(rnd.NextDouble() * 16, rnd.NextDouble() * 12, rnd.NextDouble() * 5);
            return nodes.Select(k => Log10Distance(truth, solver.NodeLocation(k)) + 0.05 * (rnd.NextDouble() - 0.5)).ToArray();
        }).ToArray();
        foreach (var m in ms.Take(200)) solver.Fit(nodes, m, w, full, loo, scratch); // warm up
        // CPU time rather than wall clock, so a busy machine does not inflate it.
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        foreach (var m in ms) solver.Fit(nodes, m, w, full, Span<double>.Empty, scratch);
        var fullOnly = Process.GetCurrentProcess().TotalProcessorTime - cpu;
        cpu = Process.GetCurrentProcess().TotalProcessorTime;
        foreach (var m in ms) solver.Fit(nodes, m, w, full, loo, scratch);
        var withLoo = Process.GetCurrentProcess().TotalProcessorTime - cpu;
        TestContext.Out.WriteLine($"{solver.Cells} cells, {House.Length} nodes: {fullOnly.TotalMilliseconds * 1000 / n:0.0} us CPU per window full fit only, {withLoo.TotalMilliseconds * 1000 / n:0.0} us with leave-one-out");
    }
}
