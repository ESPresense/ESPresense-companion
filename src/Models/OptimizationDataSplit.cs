namespace ESPresense.Models;

public sealed record OptimizationDataSplit(
    OptimizationSnapshot Training,
    IReadOnlyList<OptimizationSnapshot> Validation)
{
    // Two held-out snapshots at least: the every-snapshot check in RunCycleAsync needs more than one to mean anything.
    public const int MinimumValidationSnapshots = 2;
    public const int MinimumSnapshots = MinimumValidationSnapshots + 2;

    /// <summary>
    /// The snapshots, oldest first, whose measures were pooled into <see cref="Training"/>. Read this rather than
    /// re-deriving the split, so a report on the training set always describes the snapshots it was built from.
    /// </summary>
    public IReadOnlyList<OptimizationSnapshot> TrainingSnapshots { get; init; } = Array.Empty<OptimizationSnapshot>();

    public static bool TryCreate(
        IEnumerable<OptimizationSnapshot> snapshots,
        double validationFraction,
        out OptimizationDataSplit? split)
    {
        var ordered = snapshots.OrderBy(snapshot => snapshot.Timestamp).ToArray();
        if (ordered.Length < MinimumSnapshots)
        {
            split = null;
            return false;
        }

        var fraction = Math.Clamp(validationFraction, 0.1, 0.5);
        var validationCount = Math.Max(MinimumValidationSnapshots, (int)Math.Ceiling(ordered.Length * fraction));
        var trainingCount = ordered.Length - validationCount;
        if (trainingCount < 2)
        {
            split = null;
            return false;
        }

        var trainingSnapshots = ordered.Take(trainingCount).ToArray();
        var training = new OptimizationSnapshot
        {
            Timestamp = trainingSnapshots[^1].Timestamp,
            // One measure per (tx, rx, rxAdj-at-capture) link, averaged across the pooled snapshots
            // (issue #1868): the same static link sampled every 30 s is not dozens of independent
            // observations, and pooling them raw lets the fit chase sampling noise on a few links.
            Measures = trainingSnapshots.SelectMany(snapshot => snapshot.Measures)
                .GroupBy(m => m.Tx?.Id != null && m.Rx?.Id != null ? $"{m.Tx.Id}|{m.Rx.Id}|{m.RssiRxAdj}" : null)
                .Select(g =>
                {
                    var first = g.First();
                    var n = g.Count();
                    if (g.Key == null) return first;
                    return new Measure
                    {
                        Tx = first.Tx,
                        Rx = first.Rx,
                        Rssi = g.Average(m => m.Rssi),
                        RssiRxAdj = first.RssiRxAdj,
                        RssiVar = g.Count(m => m.RssiVar is > 0) > 0
                            ? g.Where(m => m.RssiVar is > 0).Average(m => m.RssiVar!.Value) : first.RssiVar,
                        RefRssi = first.RefRssi,
                        Distance = g.Average(m => m.Distance),
                        DistVar = first.DistVar,
                    };
                }).ToList()
        };

        split = new OptimizationDataSplit(training, ordered.Skip(trainingCount).ToArray()) { TrainingSnapshots = trainingSnapshots };
        return true;
    }
}
