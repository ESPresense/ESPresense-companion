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
                    _state.OptimizerState.Optimizers = "Disabled";
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    optimization = _state.Config?.Optimization;
                }

                // Try to acquire the optimization lease (wait indefinitely)
                _state.OptimizerState.Optimizers = "Acquiring lease...";
                await using var lease = await _leaseService.AcquireAsync(
                    OptimizationLeaseName,
                    timeout: null, // Wait indefinitely
                    cancellationToken: stoppingToken)
                    ?? throw new InvalidOperationException("Optimization lease acquisition returned null unexpectedly.");

                Log.Information("Optimization enabled");
                _state.OptimizerState.Optimizers = "Starting...";
                await Task.Delay(TimeSpan.FromSeconds(optimization.IntervalSecs), stoppingToken);

                for (int i = 0; i < 3; i++)
                {
                    _state.TakeOptimizationSnapshot();
                    await Task.Delay(TimeSpan.FromSeconds(optimization.IntervalSecs), stoppingToken);
                }

                Log.Information("Optimization cycle started");
                _previousBestCorr = double.NaN;
                _previousBestRmse = double.NaN;

                while (lease.HasLease())
                {
                    optimization = _state.Config?.Optimization;
                    if (optimization is not { Enabled: true }) break;
                    var os = _state.TakeOptimizationSnapshot();

                    IList<IOptimizer> currentOptimizers;
                    lock (_optimizersLock)
                        currentOptimizers = _optimizers.ToList();
                    _state.OptimizerState.Optimizers = string.Join(", ", currentOptimizers.Select(o => o.Name));

                    await RunCycleAsync(os, _state.OptimizationSnaphots, optimization, currentOptimizers);

                    await Task.Delay(TimeSpan.FromSeconds(optimization.IntervalSecs), stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
            catch (Exception ex)
            {
                _state.OptimizerState.Optimizers = "Error: " + ex.Message;
                Log.Error(ex, "Error in OptimizationRunner");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
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

        var baselineResults = new OptimizationResults();
        var (bestCorr, bestRmse, baselineSamples) = baselineResults.Evaluate(evalSet, _nsd);
        if (baselineSamples < optimization.MinSamples)
        {
            Log.Debug("Optimization skipped: baseline evaluated on {0} measurements, need at least {1} (min_samples)", baselineSamples, optimization.MinSamples);
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

        var currentSettings = os.GetNodeIds().ToDictionary(id => id, _nsd.Get);

        // Every optimizer is scored against the same incumbent and only the best passing proposal is
        // applied. Applying them greedily in sequence made the outcome depend on list order, and let an
        // absorption-only proposal land on top of another optimizer's rxAdj: a pair nobody fitted.
        (IOptimizer Optimizer, OptimizationResults Results, double Composite, double Corr, double Rmse)? best = null;

        foreach (var optimizer in optimizers)
        {
            var results = optimizer.Optimize(os, currentSettings);
            var (corr, rmse, samples) = results.Evaluate(evalSet, _nsd);
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
                var (dCorr, dRmse, _) = results.Evaluate(evalSet, _nsd, deviceFrame: true);
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
                    var (bc, br, bs) = baselineResults.Evaluate(one, _nsd);
                    var (pc, pr, ps) = results.Evaluate(one, _nsd);
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

        if (best == null) return;

        var (chosen, chosenResults, chosenComposite, chosenCorr, chosenRmse) = best.Value;
        Log.Information("Applying {0} (Composite={1:0.000}, R={2:0.000}, RMSE={3:0.000})", chosen.Name, chosenComposite, chosenCorr, chosenRmse);
        _state.OptimizerState.BestR = chosenCorr;
        _state.OptimizerState.BestRMSE = chosenRmse;

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
