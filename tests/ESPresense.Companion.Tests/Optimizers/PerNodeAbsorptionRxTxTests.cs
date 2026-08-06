using ESPresense.Models;
using ESPresense.Optimizers;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Companion.Tests.Optimizers;

public class PerNodeAbsorptionRxTxTests
{
    [Test]
    public void Optimize_DoesNotRailMostAbsorptionsFromOneOutlier()
    {
        var optimization = new ConfigOptimization
        {
            HuberDelta = 2,
            Limits = new Dictionary<string, double>
            {
                ["absorption_min"] = 2.5,
                ["absorption_max"] = 3.5,
                ["rx_adj_rssi_min"] = -5,
                ["rx_adj_rssi_max"] = 25,
                ["tx_ref_rssi_min"] = -80,
                ["tx_ref_rssi_max"] = -40
            }
        };
        var optimizer = new PerNodeAbsorptionRxTx(optimization);
        var nodes = new[]
        {
            new OptNode { Id = "a", Location = new Point3D(0, 0, 0) },
            new OptNode { Id = "b", Location = new Point3D(4, 0, 0) },
            new OptNode { Id = "c", Location = new Point3D(0, 7, 0) },
            new OptNode { Id = "d", Location = new Point3D(11, 5, 0) }
        };
        var txRefRssi = nodes.ToDictionary(node => node.Id, _ => -59.0);
        var snapshot = new OptimizationSnapshot();

        foreach (var rx in nodes)
        foreach (var tx in nodes.Where(node => node.Id != rx.Id))
        {
            var distance = rx.Location.DistanceTo(tx.Location);
            snapshot.Measures.Add(new Measure
            {
                Rx = rx,
                Tx = tx,
                Rssi = txRefRssi[tx.Id] - 30 * Math.Log10(distance),
                RssiRxAdj = 0,
                RssiVar = 1,
                RefRssi = txRefRssi[tx.Id]
            });
        }

        // A robust objective should not let one bad reading drive every receiver to a bound.
        snapshot.Measures[0].Rssi += 30;
        var settings = nodes.ToDictionary(
            node => node.Id,
            node => new NodeSettings(node.Id)
            {
                Calibration = new CalibrationSettings
                {
                    Absorption = 3.5,
                    RxAdjRssi = 0,
                    TxRefRssi = (int)txRefRssi[node.Id]
                }
            });

        var results = optimizer.Optimize(snapshot, settings).QuantizeForApplication();
        var absorptions = nodes.Select(node => results.Nodes[node.Id].Absorption!.Value).ToArray();

        Assert.That(absorptions.Count(value => value is >= 2.8 and <= 3.2), Is.GreaterThanOrEqualTo(3));
        Assert.That(absorptions.Count(value => value == optimization.AbsorptionMin || value == optimization.AbsorptionMax), Is.LessThan(2));
    }
}
