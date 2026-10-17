using ESPresense.Models;
using ESPresense.Services;
using ESPresense.Utils;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ESPresense.Optimizers;

internal class OptimizationRunner : BackgroundService
{
    private const string OptimizationLeaseName = "optimization";
    private readonly State _state;
    private readonly NodeSettingsStore _nsd;
    private readonly ILogger<OptimizationRunner> _logger;
    private readonly ConfigLoader _cfg;
    private readonly ILeaseService _leaseService;
    private IList<IOptimizer> _optimizers;
    private readonly object _optimizersLock = new();
    private string? _lastOptimizerMode;
    private double _previousBestCorr = double.NaN;
    private double _previousBestRmse = double.NaN;

    public OptimizationRunner(State state, NodeSettingsStore nsd, ILogger<OptimizationRunner> logger, ConfigLoader cfg, ILeaseService leaseService)
    {
        _state = state;
        _nsd = nsd;
        _logger = logger;
        _cfg = cfg;
        _leaseService = leaseService;
        _optimizers = new List<IOptimizer>();

        _cfg.ConfigChanged += (_, config) =>
        {
            var optimizerMode = config?.Optimization?.Optimizer?.ToLowerInvariant() ?? "legacy";
            lock (_optimizersLock)
            {
                if (_lastOptimizerMode == optimizerMode)
                    return;
                _optimizers = BuildOptimizers(optimizerMode);
                _lastOptimizerMode = optimizerMode;
                Log.Information("Optimizer mode changed to {0}", optimizerMode);
            }
        };

        var initialMode = _cfg.Config?.Optimization?.Optimizer?.ToLowerInvariant() ?? "legacy";
        _optimizers = BuildOptimizers(initialMode);
        _lastOptimizerMode = initialMode;
    }

    private IList<IOptimizer> BuildOptimizers(string? mode)
    {
        mode = mode?.ToLowerInvariant() ?? "legacy";
        return mode switch
        {
            "global_absorption" => new List<IOptimizer> { new GlobalAbsorptionRxTxOptimizer(_state) },
            "per_node_absorption" => new List<IOptimizer> { new PerNodeAbsorptionRxTx(_state) },
            _ => new List<IOptimizer>
            {
                new RxAdjRssiOptimizer(_state),
                new AbsorptionAvgOptimizer(_state),
                new AbsorptionErrOptimizer(_state),
                new IsotonicRegressionOptimizer(_state)
            }
        };
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var optimization = _state.Config?.Optimization;
                while (optimization is not { Enabled: true })
                {
                    Log.Information("Optimization disabled");
                    SetStatus("Disabled", "Auto optimization is disabled.", optimizers: string.Empty);
                    _state.OptimizerState.NextRunAt = null;
                    _state.OptimizerState.LeaseHolder = null;
                    _state.OptimizerState.LeaseExpiresAt = null;
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    optimization = _state.Config?.Optimization;
                }

                // Try to acquire the optimization lease (wait indefinitely)
                SetStatus("WaitingForLease", "Waiting for MQTT connection or optimization lease availability.", optimizers: "Acquiring lease...");
                var leaseTask = _leaseService.AcquireAsync(
                    OptimizationLeaseName,
                    timeout: null, // Wait indefinitely
                    cancellationToken: stoppingToken);

                while (!leaseTask.IsCompleted)
                {
                    UpdateLeaseStatus();
                    await Task.WhenAny(leaseTask, Task.Delay(TimeSpan.FromSeconds(1), stoppingToken));
                }

                await using var lease = await leaseTask
                    ?? throw new InvalidOperationException("Optimization lease acquisition returned null unexpectedly.");

                Log.Information("Optimization enabled");
                SetStatus("Collecting", "Collecting snapshots for the initial training and validation split.", optimizers: GetOptimizerNames());
                _state.OptimizerState.LeaseHolder = "This instance";
                _state.OptimizerState.LeaseExpiresAt = _leaseService.GetStatus(OptimizationLeaseName)?.ExpiresAt;
                _previousBestCorr = double.NaN;
                _previousBestRmse = double.NaN;
                DateTime? lastOptimizationAt = null;

                // Sampling and optimizing run on separate clocks: snapshots are taken every sample interval,
                // while proposals are only evaluated once per optimization interval on the accumulated history.
                while (lease.HasLease())
                {
                    optimization = _state.Config?.Optimization;
                    if (optimization is not { Enabled: true }) break;
                    _state.TakeOptimizationSnapshot();
                    UpdateSampleCounts();

                    var now = DateTime.UtcNow;
                    var optimizationDue = lastOptimizationAt == null ||
                                          now - lastOptimizationAt.Value >= TimeSpan.FromSeconds(optimization.EffectiveOptimizationIntervalSecs);
                    if (optimizationDue)
                    {
                        IList<IOptimizer> currentOptimizers;
                        lock (_optimizersLock)
                            currentOptimizers = _optimizers.ToList();

                        if (await RunScheduledCycleAsync(optimization, currentOptimizers))
                        {
                            lastOptimizationAt = now;
                            _state.OptimizerState.NextRunAt = now.AddSeconds(optimization.EffectiveOptimizationIntervalSecs);
                            SetStatus("Waiting", _state.OptimizerState.LastOutcome ?? "Validation cycle completed.");
                        }
                    }
                    else
                    {
                        SetStatus("Waiting", $"Collecting fresh samples; next validation at {_state.OptimizerState.NextRunAt:O}.");
                    }

                    await Task.Delay(TimeSpan.FromSeconds(optimization.EffectiveSampleIntervalSecs), stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
            catch (Exception ex)
            {
                SetStatus("Error", ex.Message, optimizers: "Error: " + ex.Message);
                _state.OptimizerState.NextRunAt = null;
                Log.Error(ex, "Error in OptimizationRunner");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    /// <summary>Share of the training window the history must span before the first proposal is evaluated.</summary>
    internal const double MinimumHistoryFraction = 0.5;

    /// <summary>
    /// A few seconds of history is enough to split but not enough to trust: a proposal fitted to two snapshots and
    /// checked on one would pass the held-out and every-snapshot checks almost vacuously, and would be pushed to
    /// real nodes within minutes of start-up. Wait until the history covers half the training window.
    /// </summary>
    internal static bool HasSufficientHistory(IReadOnlyCollection<OptimizationSnapshot> snapshots, ConfigOptimization optimization)
    {
        if (snapshots.Count < OptimizationDataSplit.MinimumSnapshots) return false;
        var span = snapshots.Max(s => s.Timestamp) - snapshots.Min(s => s.Timestamp);
        return span >= TimeSpan.FromMinutes(optimization.EffectiveTrainingWindowMins * MinimumHistoryFraction);
    }

    /// <summary>
    /// Splits the snapshot history by time: the older part is pooled into one training snapshot that the
    /// optimizers fit, and the newest <see cref="ConfigOptimization.ValidationFraction"/> is held out so
    /// <see cref="RunCycleAsync"/> only scores proposals on data they were not fitted to.
    /// Returns false (and keeps collecting) when there is not yet enough history for a split.
    /// </summary>
    internal async Task<bool> RunScheduledCycleAsync(ConfigOptimization optimization, IList<IOptimizer> currentOptimizers)
    {
        var snapshots = _state.OptimizationSnaphots.ToArray();
        if (!HasSufficientHistory(snapshots, optimization))
        {
            SetStatus("Collecting", $"Collected {snapshots.Length} snapshots; waiting until they span {MinimumHistoryFraction:P0} of the {optimization.EffectiveTrainingWindowMins}-minute training window before the first validation.");
            return false;
        }

        if (!OptimizationDataSplit.TryCreate(snapshots, optimization.EffectiveValidationFraction, out var split) || split == null)
        {
            SetStatus("Collecting", $"Collected {snapshots.Length} of {OptimizationDataSplit.MinimumSnapshots} required snapshots.");
            Log.Information("Optimization deferred: only {SnapshotCount} snapshots are available", snapshots.Length);
            return false;
        }

        _state.OptimizerState.Optimizers = string.Join(", ", currentOptimizers.Select(o => o.Name));
        _state.OptimizerState.TrainingSamples = split.Training.Measures.Count;
        SetStatus("Optimizing", $"Training on {split.Training.Measures.Count} measurements and validating newer holdout data.");

        await RunCycleAsync(split.Training, split.Validation.ToList(), optimization, currentOptimizers);
        _state.OptimizerState.LastRunAt = DateTime.UtcNow;
        return true;
    }

    private void SetStatus(string phase, string message, string? optimizers = null)
    {
        _state.OptimizerState.Phase = phase;
        _state.OptimizerState.Message = message;
        if (optimizers != null) _state.OptimizerState.Optimizers = optimizers;
    }

    private string GetOptimizerNames()
    {
        lock (_optimizersLock)
            return string.Join(", ", _optimizers.Select(optimizer => optimizer.Name));
    }

    private void UpdateSampleCounts()
    {
        _state.OptimizerState.SnapshotCount = _state.OptimizationSnaphots.Count;
        _state.OptimizerState.MeasurementCount = _state.OptimizationSnaphots.Sum(snapshot => snapshot.Measures.Count);
        _state.OptimizerState.LeaseExpiresAt = _leaseService.GetStatus(OptimizationLeaseName)?.ExpiresAt;
    }

    private void UpdateLeaseStatus()
    {
        var status = _leaseService.GetStatus(OptimizationLeaseName);
        if (status == null || status.InstanceId == "nobody" || status.ExpiresAt <= DateTime.UtcNow)
        {
            _state.OptimizerState.LeaseHolder = null;
            _state.OptimizerState.LeaseExpiresAt = null;
            _state.OptimizerState.Message = "Waiting for MQTT connection or lease confirmation.";
            return;
        }

        _state.OptimizerState.LeaseHolder = status.IsOwned ? "This instance" : status.InstanceId;
        _state.OptimizerState.LeaseExpiresAt = status.ExpiresAt;
        _state.OptimizerState.Message = status.IsOwned
            ? "Confirming this instance's optimization lease."
            : $"Lease held by {status.InstanceId} until {status.ExpiresAt:O}.";
    }

    /// <summary>
    /// Runs one optimization pass: evaluates the incumbent settings over <paramref name="oss"/>, then each
    /// optimizer's proposal for <paramref name="os"/>, and applies the single best proposal that passes
    /// every check below. The checks exist because a calibration that is wrong but scores well is pushed
    /// to real nodes over MQTT, and the history of this gate is a history of proposals that did exactly that.
    ///
    /// Min samples: an evaluation backed by fewer than <see cref="ConfigOptimization.MinSamples"/>
    /// measurements is rejected outright; with a handful of points a proposal can fit the very samples
    /// it was derived from (RMSE ~ 0) and "beat" the baseline without having been validated by anything.
    ///
    /// Held-out scoring: when the history holds snapshots besides the one the optimizers fitted, proposals
    /// are scored only on those others. A proposal that memorised its own snapshot's radio noise scores
    /// well on itself however wrong it is, and on a cold start the gate would wave it straight through.
    ///
    /// Device frame: a proposal that moves txRef must also win with txRef held, because node-to-node data
    /// cannot tell "every transmitter k dB loud" from "every receiver k dB deaf" while every device, ranged
    /// with its own rssi@1m, can. Scoring in RSSI space is blind to that shift; the robot survey showed it
    /// as rxAdj climbing cycle after cycle.
    ///
    /// Every snapshot: the proposal must beat the incumbent on each eval snapshot on its own, not just on
    /// the aggregate. rxAdj and absorption trade off almost perfectly over the narrow band of distances
    /// one receiver sees, so on a home that has not changed, noise alone used to move settings by several
    /// dB every cycle along that ridge.
    ///
    /// Best of: proposals are all scored against the same incumbent and only the best is applied, rather
    /// than applied greedily in list order, which let an absorption-only proposal land on top of another
    /// optimizer's rxAdj: a pair nobody fitted.
    /// </summary>
    internal async Task RunCycleAsync(OptimizationSnapshot os, List<OptimizationSnapshot> oss, ConfigOptimization optimization, IList<IOptimizer> optimizers)
    {
        var evalSet = oss.Where(s => !ReferenceEquals(s, os)).ToList();
        if (evalSet.Count == 0) evalSet = oss;

        // Freeze the incumbent settings once for the whole cycle. NodeSettingsStore.Get reads whatever the
        // store holds at that moment, so scoring straight from it would let the same proposal score
        // differently depending on whether an earlier cycle's MQTT writes had been echoed back yet.
        var frozen = new Dictionary<string, NodeSettings>(StringComparer.Ordinal);
        foreach (var id in os.GetNodeIds().Concat(evalSet.SelectMany(s => s.GetNodeIds())).Distinct())
            frozen[id] = _nsd.Get(id);
        NodeSettings Frozen(string id) => frozen.TryGetValue(id, out var settings) ? settings : _nsd.Get(id);

        var baselineResults = new OptimizationResults();
        var (bestCorr, bestRmse, baselineSamples) = baselineResults.Evaluate(evalSet, Frozen);
        _state.OptimizerState.ValidationSamples = baselineSamples;
        if (baselineSamples < optimization.MinSamples)
        {
            Log.Debug("Optimization skipped: baseline evaluated on {0} measurements, need at least {1} (min_samples)", baselineSamples, optimization.MinSamples);
            _state.OptimizerState.LastOutcome = $"Skipped: only {baselineSamples} validation measurements, need at least {optimization.MinSamples} (min_samples).";
            return;
        }

        // Use weights from ConfigOptimization
        double correlationWeight = optimization.CorrelationWeight;
        double rmseWeight = optimization.RmseWeight;
        double bestScore = (bestCorr * correlationWeight) + ((1 - bestRmse / (1 + bestRmse)) * rmseWeight);

        if (double.IsNaN(_previousBestCorr) || double.IsNaN(_previousBestRmse) || Math.Abs(bestCorr - _previousBestCorr) > 0.001 || Math.Abs(bestRmse - _previousBestRmse) > 0.001)
        {
            Log.Information("Baseline metrics: Composite={2:0.000} (R={0:0.000}, RMSE={1:0.000})", bestCorr, bestRmse, bestScore);
            _previousBestCorr = bestCorr;
            _previousBestRmse = bestRmse;
        }

        _state.OptimizerState.BestR = bestCorr;
        _state.OptimizerState.BestRMSE = bestRmse;

        // Optimizers get their own copies so nothing they do can touch the frozen incumbent.
        var currentSettings = os.GetNodeIds().ToDictionary(id => id, id => Frozen(id).Clone());

        // Every optimizer is scored against the same incumbent and only the best passing proposal is
        // applied. Applying them greedily in sequence made the outcome depend on list order, and let an
        // absorption-only proposal land on top of another optimizer's rxAdj: a pair nobody fitted.
        (IOptimizer Optimizer, OptimizationResults Results, double Composite, double Corr, double Rmse)? best = null;

        foreach (var optimizer in optimizers)
        {
            var results = optimizer.Optimize(os, currentSettings).QuantizeForApplication();
            var (corr, rmse, samples) = results.Evaluate(evalSet, Frozen);
            if (samples < optimization.MinSamples)
            {
                Log.Debug("Optimizer {0,-24} rejected: evaluated on {1} measurements, need at least {2} (min_samples)",
                    optimizer.Name, samples, optimization.MinSamples);
                continue;
            }

            // Use weights from ConfigOptimization
            var composite = (corr * correlationWeight) + ((1 - rmse / (1 + rmse)) * rmseWeight);

            if (double.IsNaN(composite) || double.IsInfinity(composite) || composite <= bestScore)
            {
                Log.Information("Optimizer {0,-24} found worse results: Composite={1:0.000} <= Best={2:0.000} (R={3:0.000}, RMSE={4:0.000})",
                    optimizer.Name, composite, bestScore, corr, rmse);

                foreach (var (id, result) in results.Nodes)
                    Log.Debug("Rejected {0,-20}: Absorption={1:0.00}, RxAdj={2:00}, TxAdj={3:00}, Error={4}",
                        id, result.Absorption, result.RxAdjRssi, result.TxRefRssi, result.Error);
                continue;
            }

            // A proposal that moves txRef must also win with txRef held where it is: devices are ranged
            // with their own rssi@1m, so a txRef/rxAdj shift that cancels node-to-node is a plain range
            // bias for every device. See OptimizationResults.Evaluate.
            if (results.Nodes.Values.Any(r => r.TxRefRssi != null))
            {
                var (dCorr, dRmse, _) = results.Evaluate(evalSet, Frozen, deviceFrame: true);
                var deviceComposite = (dCorr * correlationWeight) + ((1 - dRmse / (1 + dRmse)) * rmseWeight);
                if (double.IsNaN(deviceComposite) || double.IsInfinity(deviceComposite) || deviceComposite <= bestScore)
                {
                    Log.Information("Optimizer {0,-24} rejected: better node-to-node (Composite={1:0.000}) but not with txRef held (Composite={2:0.000} <= Best={3:0.000}); the gain is a txRef/rxAdj shift devices would see as range bias",
                        optimizer.Name, composite, deviceComposite, bestScore);
                    continue;
                }
            }

            // On a home that has not changed, successive snapshots differ only by noise, and rxAdj and
            // absorption trade off almost perfectly over the narrow band of distances one receiver sees.
            // A proposal that is ahead on the aggregate because one snapshot's noise favours it would be
            // applied, then undone by the next cycle's noise, and settings wander along that ridge
            // indefinitely. So it must also win against the incumbent on every eval snapshot by itself.
            if (evalSet.Count > 1)
            {
                var losesOn = evalSet.Where(snap =>
                {
                    var one = new List<OptimizationSnapshot> { snap };
                    var (bc, br, bs) = baselineResults.Evaluate(one, Frozen);
                    var (pc, pr, ps) = results.Evaluate(one, Frozen);
                    if (bs < optimization.MinSamples || ps < optimization.MinSamples) return false;
                    var baseOne = (bc * correlationWeight) + ((1 - br / (1 + br)) * rmseWeight);
                    var propOne = (pc * correlationWeight) + ((1 - pr / (1 + pr)) * rmseWeight);
                    return double.IsNaN(propOne) || propOne <= baseOne;
                }).Count();
                if (losesOn > 0)
                {
                    Log.Information("Optimizer {0,-24} rejected: better on the aggregate (Composite={1:0.000} > Best={2:0.000}) but not on {3} of {4} snapshots on their own; treating the gain as noise",
                        optimizer.Name, composite, bestScore, losesOn, evalSet.Count);
                    continue;
                }
            }

            Log.Information("Optimizer {0,-24} found better results: Composite={1:0.000} > Best={2:0.000} (R={3:0.000}, RMSE={4:0.000})",
                optimizer.Name, composite, bestScore, corr, rmse);
            if (best == null || composite > best.Value.Composite)
                best = (optimizer, results, composite, corr, rmse);
        }

        if (best == null)
        {
            _state.OptimizerState.LastOutcome = "No proposal beat the current calibration on the held-out data.";
            return;
        }

        var (chosen, chosenResults, chosenComposite, chosenCorr, chosenRmse) = best.Value;
        Log.Information("Applying {0} (Composite={1:0.000}, R={2:0.000}, RMSE={3:0.000})", chosen.Name, chosenComposite, chosenCorr, chosenRmse);
        _state.OptimizerState.BestR = chosenCorr;
        _state.OptimizerState.BestRMSE = chosenRmse;
        _state.OptimizerState.LastOutcome = $"Applied {chosen.Name} to {chosenResults.Nodes.Count} nodes (held-out R={chosenCorr:0.000}, RMSE={chosenRmse:0.000}).";

        foreach (var (id, result) in chosenResults.Nodes)
        {
            Log.Information("Applied {0,-20}: Absorption={1:0.00}, RxAdj={2:00}, TxAdj={3:00}, Error={4}",
                id, result.Absorption, result.RxAdjRssi, result.TxRefRssi, result.Error);

            var nodeSettings = _nsd.Get(id);
            if (result.Absorption != null) nodeSettings.Calibration.Absorption = result.Absorption;
            if (result.RxAdjRssi != null) nodeSettings.Calibration.RxAdjRssi = (int?)Math.Round(result.RxAdjRssi.Value);
            if (result.TxRefRssi != null) nodeSettings.Calibration.TxRefRssi = (int?)Math.Round(result.TxRefRssi.Value);
            await _nsd.Set(id, nodeSettings);
        }
    }
}
