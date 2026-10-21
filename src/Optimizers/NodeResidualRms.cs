using ESPresense.Models;

namespace ESPresense.Optimizers;

/// <summary>
/// Accumulates a weighted RMS of the fit residual for each node, over every measurement it took part in,
/// whether it heard it or sent it. A node that is both a receiver and a transmitter gets one figure across
/// both roles, which is what <see cref="ProposedValues.Error"/> can hold: how well the fitted model explains
/// the links that node is on.
/// </summary>
internal sealed class NodeResidualRms
{
    private readonly Dictionary<string, (double SumSq, double Weight)> _byNode = new();

    public void Add(Measure m, double residual, double weight)
    {
        if (!double.IsFinite(residual) || !(weight > 0)) return;
        Accumulate(m.Rx.Id, residual, weight);
        Accumulate(m.Tx.Id, residual, weight);
    }

    private void Accumulate(string id, double residual, double weight)
    {
        var (sumSq, w) = _byNode.GetValueOrDefault(id);
        _byNode[id] = (sumSq + weight * residual * residual, w + weight);
    }

    public double? Get(string id) =>
        _byNode.TryGetValue(id, out var acc) && acc.Weight > 0 ? Math.Sqrt(acc.SumSq / acc.Weight) : null;
}
