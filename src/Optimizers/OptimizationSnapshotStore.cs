using ESPresense.Models;

namespace ESPresense.Optimizers;

/// <summary>
/// The rolling history of node-to-node and anchor measurements the optimizers train and validate on.
/// Owned by <see cref="OptimizationRunner"/>, which alone writes it; <see cref="ESPresense.Services.OptimizerDebugService"/>
/// also reads it, through <see cref="ToArray"/>, to report on the history.
/// </summary>
public sealed class OptimizationSnapshotStore
{
    private readonly object _lock = new();
    private readonly List<OptimizationSnapshot> _snapshots = new();

    /// <summary>
    /// Records the current measurements from <paramref name="state"/> and drops snapshots older than the
    /// configured training window (30 minutes by default).
    /// </summary>
    /// <returns>The new snapshot.</returns>
    public OptimizationSnapshot Take(State state)
    {
        var os = state.ReadMeasures();
        var expiryMinutes = state.Config?.Optimization?.EffectiveTrainingWindowMins ?? 30;
        var expiryThreshold = DateTime.UtcNow.AddMinutes(-expiryMinutes);
        lock (_lock)
        {
            _snapshots.RemoveAll(s => s.Timestamp < expiryThreshold);
            _snapshots.Add(os);
        }
        return os;
    }

    /// <summary>Adds snapshots as-is, without expiring anything.</summary>
    public void AddRange(IEnumerable<OptimizationSnapshot> snapshots)
    {
        lock (_lock) _snapshots.AddRange(snapshots);
    }

    public OptimizationSnapshot[] ToArray()
    {
        lock (_lock) return _snapshots.ToArray();
    }

    public int Count
    {
        get { lock (_lock) return _snapshots.Count; }
    }

    public int MeasurementCount
    {
        get { lock (_lock) return _snapshots.Sum(s => s.Measures.Count); }
    }
}
