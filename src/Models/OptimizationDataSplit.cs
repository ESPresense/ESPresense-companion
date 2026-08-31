namespace ESPresense.Models;

public sealed record OptimizationDataSplit(
    OptimizationSnapshot Training,
    IReadOnlyList<OptimizationSnapshot> Validation)
{
    public static bool TryCreate(
        IEnumerable<OptimizationSnapshot> snapshots,
        double validationFraction,
        out OptimizationDataSplit? split)
    {
        var ordered = snapshots.OrderBy(snapshot => snapshot.Timestamp).ToArray();
        if (ordered.Length < 3)
        {
            split = null;
            return false;
        }

        var fraction = Math.Clamp(validationFraction, 0.1, 0.5);
        var validationCount = Math.Max(1, (int)Math.Ceiling(ordered.Length * fraction));
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
            Measures = trainingSnapshots.SelectMany(snapshot => snapshot.Measures).ToList()
        };

        split = new OptimizationDataSplit(training, ordered.Skip(trainingCount).ToArray());
        return true;
    }
}
