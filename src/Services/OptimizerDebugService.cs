using ESPresense.Models;
using ESPresense.Optimizers;

namespace ESPresense.Services;

/// <summary>
/// What the optimizer sees, what each optimizer proposed, how it scored and which gate refused it (#1857).
/// The one implementation behind the MCP optimizer tools (<see cref="McpOptimizerTools"/>) and /api/optimizer.
/// Read-only: nothing here writes node settings or publishes to MQTT, the dry run included.
/// </summary>
public class OptimizerDebugService
{
    private readonly OptimizationRunner _runner;
    private readonly State _state;

    internal OptimizerDebugService(OptimizationRunner runner, State state)
    {
        _runner = runner;
        _state = state;
    }

    private ConfigOptimization Optimization => _state.Config?.Optimization ?? new ConfigOptimization();

    public OptimizerStatus GetStatus()
    {
        var optimization = Optimization;
        var os = _state.OptimizerState;
        var snapshots = _runner.Snapshots.ToArray();
        DateTime? oldest = snapshots.Length > 0 ? snapshots.Min(s => s.Timestamp) : null;
        DateTime? newest = snapshots.Length > 0 ? snapshots.Max(s => s.Timestamp) : null;

        OptimizerSplitSizes? split = null;
        if (OptimizationDataSplit.TryCreate(snapshots, optimization.EffectiveValidationFraction, out var s) && s != null)
            split = new OptimizerSplitSizes(
                s.TrainingSnapshots.Count, s.Training.Measures.Count,
                s.Validation.Count, s.Validation.Sum(v => v.Measures.Count));

        return new OptimizerStatus(
            optimization.Enabled,
            _runner.Mode,
            _runner.ConfiguredMode,
            os.Phase,
            os.Message,
            _runner.OptimizerNames,
            os.LeaseHolder,
            os.LeaseExpiresAt,
            snapshots.Length,
            snapshots.Sum(x => x.Measures.Count),
            oldest,
            newest,
            oldest != null ? Math.Round((newest!.Value - oldest.Value).TotalMinutes, 2) : null,
            OptimizationRunner.HasSufficientHistory(snapshots, optimization),
            split,
            os.LastRunAt,
            os.NextRunAt,
            os.LastOutcome,
            os.BestR,
            os.BestRMSE,
            OptimizerGateSettings.From(optimization));
    }

    /// <summary>The last live cycle's report, or null if none has run since start-up.</summary>
    public OptimizerCycleReport? GetLastCycle() => _runner.LastCycle?.Report;

    /// <summary>
    /// Residuals of the last live cycle, or (with <paramref name="dryRun"/>) of a dry cycle run now.
    /// Null when there is no live cycle yet or it was skipped for too few validation measures.
    /// </summary>
    /// <exception cref="OptimizerUnavailableException">A dry run was asked for and there is too little history.</exception>
    /// <exception cref="UnknownOptimizerException">A dry run was asked for an optimizer that does not exist.</exception>
    public async Task<OptimizerResidualsReport?> GetResidualsAsync(bool dryRun = false, string? optimizer = null, CancellationToken cancellationToken = default)
    {
        if (!dryRun) return _runner.LastCycle?.Residuals;
        return (await _runner.DryRunAsync(optimizer, cancellationToken)).Residuals;
    }

    /// <summary>Runs one cycle now on the current history and reports it, applying nothing.</summary>
    /// <exception cref="OptimizerUnavailableException">Too little history to split.</exception>
    /// <exception cref="UnknownOptimizerException">No optimizer has that name.</exception>
    public async Task<OptimizerCycleReport> RunDryCycleAsync(string? optimizer = null, CancellationToken cancellationToken = default)
        => (await _runner.DryRunAsync(optimizer, cancellationToken)).Report;

    /// <summary>
    /// The pooled training snapshot the next cycle would fit, one cell per tx/rx pair, optionally only the pairs
    /// <paramref name="nodeId"/> is on (either end).
    /// </summary>
    public OptimizerMeasuresReport GetMeasures(string? nodeId = null)
    {
        if (string.IsNullOrWhiteSpace(nodeId)) nodeId = null;
        var snapshots = _runner.Snapshots.ToArray();
        var empty = new Dictionary<string, string?>();
        if (!OptimizationDataSplit.TryCreate(snapshots, Optimization.EffectiveValidationFraction, out var split) || split == null)
            return new OptimizerMeasuresReport(false,
                $"The history holds {snapshots.Length} snapshots; at least {OptimizationDataSplit.MinimumSnapshots} are needed to split off a training set." +
                (_runner.WhyNoSampling() is { } why ? " " + why : ""),
                null, null, 0, 0, empty, Array.Empty<OptimizerMeasureCell>());

        var trainingSnapshots = split.TrainingSnapshots;
        var measures = split.Training.Measures
            .Where(m => m.Tx?.Id != null && m.Rx?.Id != null)
            .Where(m => nodeId == null || string.Equals(m.Tx.Id, nodeId, StringComparison.OrdinalIgnoreCase) || string.Equals(m.Rx.Id, nodeId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var names = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var m in measures)
        {
            names.TryAdd(m.Tx.Id, m.Tx.Name);
            names.TryAdd(m.Rx.Id, m.Rx.Name);
        }

        var cells = measures
            .GroupBy(m => (Tx: m.Tx.Id, Rx: m.Rx.Id))
            .OrderBy(g => g.Key.Tx, StringComparer.Ordinal).ThenBy(g => g.Key.Rx, StringComparer.Ordinal)
            .Select(g =>
            {
                var rssi = g.Select(m => m.Rssi).ToList();
                var mean = rssi.Average();
                double? sd = rssi.Count > 1 ? Math.Sqrt(rssi.Sum(r => (r - mean) * (r - mean)) / (rssi.Count - 1)) : null;
                var vars = g.Select(m => m.RssiVar).Where(v => v is { } d && double.IsFinite(d)).Select(v => v!.Value).ToList();
                var last = g.Last();
                return new OptimizerMeasureCell(
                    g.Key.Tx,
                    g.Key.Rx,
                    g.Any(m => m.Tx.IsAnchor),
                    rssi.Count,
                    mean,
                    sd,
                    g.Select(m => m.RssiRxAdj).Distinct().OrderBy(v => v).ToList(),
                    g.Average(m => m.RefRssi),
                    vars.Count > 0 ? vars.Average() : null,
                    last.Rx.Location.DistanceTo(last.Tx.Location),
                    g.Average(m => m.Distance));
            })
            .ToList();

        return new OptimizerMeasuresReport(true, null,
            trainingSnapshots.Count > 0 ? trainingSnapshots[0].Timestamp : null,
            trainingSnapshots.Count > 0 ? trainingSnapshots[^1].Timestamp : null,
            trainingSnapshots.Count, measures.Count, names, cells);
    }
}

/// <summary>
/// An optimizer debugging request that cannot be answered yet (for example too little history); the message says
/// why. Retrying later can succeed. REST maps it to 409.
/// </summary>
public sealed class OptimizerUnavailableException(string message) : Exception(message);

/// <summary>A dry run asked for an optimizer name that does not exist; the message lists the known ones. REST maps it to 400.</summary>
public sealed class UnknownOptimizerException(string message) : ArgumentException(message);
