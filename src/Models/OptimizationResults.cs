using ESPresense.Services;
using Serilog;

namespace ESPresense.Models;

public class OptimizationResults
{

    public Dictionary<string, ProposedValues> Nodes { get; set; } = new();

    public double Evaluate(List<OptimizationSnapshot> oss, NodeSettingsStore nss)
    {
        double squaredErrorSum = 0;
        int count = 0;

        foreach (var os in oss)
        {
            foreach (var m in os.Measures)
            {
                var tx = nss.Get(m.Tx.Id);
                var rx = nss.Get(m.Rx.Id);

                Nodes.TryGetValue(m.Rx.Id, out var pv);
                double rxAdjRssi = pv?.RxAdjRssi ?? rx.Calibration.RxAdjRssi ?? 0;
                double txPower = tx.Calibration.TxRefRssi ?? -59;
                double pathLossExponent = pv?.Absorption ?? rx.Calibration.Absorption ?? 2.7;
                double distance = m.Rx.Location.DistanceTo(m.Tx.Location);
                double predictedRssi = txPower + rxAdjRssi - 10 * pathLossExponent * Math.Log10(distance);

                squaredErrorSum += Math.Pow(predictedRssi - m.Rssi, 2);
                count++;
            }
        }

        return count > 0 ? Math.Sqrt(squaredErrorSum / count) : double.MaxValue;
    }

}