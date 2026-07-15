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
                _state.OptimizerState.Optimizers = "Collecting samples...";
                var lastOptimizationAt = DateTime.UtcNow;

                while (lease.HasLease())
                {
                    optimization = _state.Config?.Optimization;
                    if (optimization is not { Enabled: true }) break;
                    _state.TakeOptimizationSnapshot();

                    var now = DateTime.UtcNow;
                    if (now - lastOptimizationAt >= TimeSpan.FromSeconds(optimization.EffectiveOptimizationIntervalSecs))
                    {
                        lastOptimizationAt = now;
                        await RunOptimizationCycle(optimization, stoppingToken);
                    }
                    else
                    {
                        _state.OptimizerState.Optimizers = $"Collecting samples ({_state.OptimizationSnaphots.Count})...";
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
                _state.OptimizerState.Optimizers = "Error: " + ex.Message;
                Log.Error(ex, "Error in OptimizationRunner");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task RunOptimizationCycle(ConfigOptimization optimization, CancellationToken stoppingToken)
    {
        var snapshots = _state.OptimizationSnaphots.ToArray();
        if (!OptimizationDataSplit.TryCreate(snapshots, optimization.EffectiveValidationFraction, out var split) || split == null)
        {
            _state.OptimizerState.Optimizers = $"Collecting samples ({snapshots.Length}, need at least 3)...";
            Log.Information("Optimization deferred: only {SnapshotCount} snapshots are available", snapshots.Length);
            return;
        }

        IList<IOptimizer> currentOptimizers;
        lock (_optimizersLock)
            currentOptimizers = _optimizers.ToList();

        _state.OptimizerState.Optimizers = string.Join(", ", currentOptimizers.Select(optimizer => optimizer.Name));
        var baseline = new OptimizationResults().EvaluateMetrics(
            split.Validation,
            _nsd,
            optimization.EffectiveHuberDelta);

        _state.OptimizerState.BestR = baseline.Correlation;
        _state.OptimizerState.BestRMSE = baseline.Rmse;
        _state.OptimizerState.BestLoss = baseline.HuberLoss;
        _state.OptimizerState.ValidationSamples = baseline.SampleCount;

        if (!baseline.IsValid)
        {
            Log.Warning("Optimization deferred: the validation set has no valid measurements");
            return;
        }

        Log.Information(
            "Validation baseline: Huber={Huber:0.000}, RMSE={RMSE:0.000}, MAE={MAE:0.000}, R={Correlation:0.000}, Samples={SampleCount}",
            baseline.HuberLoss,
            baseline.Rmse,
            baseline.Mae,
            baseline.Correlation,
            baseline.SampleCount);

        var currentSettings = split.Training.GetNodeIds().ToDictionary(id => id, _nsd.Get);
        OptimizationResults? bestResults = null;
        OptimizationMetrics bestMetrics = baseline;
        string? bestOptimizerName = null;

        foreach (var optimizer in currentOptimizers)
        {
            stoppingToken.ThrowIfCancellationRequested();

            var results = optimizer.Optimize(split.Training, currentSettings).QuantizeForApplication();
            var metrics = results.EvaluateMetrics(
                split.Validation,
                _nsd,
                optimization.EffectiveHuberDelta);
            var requiredLoss = bestMetrics.HuberLoss * (1 - optimization.EffectiveMinimumImprovement);

            if (!metrics.IsValid || metrics.HuberLoss >= requiredLoss)
            {
                Log.Information(
                    "Optimizer {Optimizer,-24} rejected: Huber={Huber:0.000} >= Required={Required:0.000} (RMSE={RMSE:0.000}, MAE={MAE:0.000}, R={Correlation:0.000})",
                    optimizer.Name,
                    metrics.HuberLoss,
                    requiredLoss,
                    metrics.Rmse,
                    metrics.Mae,
                    metrics.Correlation);
                continue;
            }

            Log.Information(
                "Optimizer {Optimizer,-24} is the best holdout candidate: Huber={Huber:0.000} < Required={Required:0.000} (RMSE={RMSE:0.000}, MAE={MAE:0.000}, R={Correlation:0.000})",
                optimizer.Name,
                metrics.HuberLoss,
                requiredLoss,
                metrics.Rmse,
                metrics.Mae,
                metrics.Correlation);
            bestResults = results;
            bestMetrics = metrics;
            bestOptimizerName = optimizer.Name;
        }

        if (bestResults == null) return;

        _state.OptimizerState.BestR = bestMetrics.Correlation;
        _state.OptimizerState.BestRMSE = bestMetrics.Rmse;
        _state.OptimizerState.BestLoss = bestMetrics.HuberLoss;
        _state.OptimizerState.ValidationSamples = bestMetrics.SampleCount;

        foreach (var (id, result) in bestResults.Nodes)
        {
            Log.Information(
                "Applied {NodeId,-20} from {Optimizer}: Absorption={Absorption:0.00}, RxAdj={RxAdj:00}, TxAdj={TxAdj:00}",
                id,
                bestOptimizerName,
                result.Absorption,
                result.RxAdjRssi,
                result.TxRefRssi);

            var nodeSettings = _nsd.Get(id);
            if (result.Absorption != null) nodeSettings.Calibration.Absorption = result.Absorption;
            if (result.RxAdjRssi != null) nodeSettings.Calibration.RxAdjRssi = (int?)Math.Round(result.RxAdjRssi.Value);
            if (result.TxRefRssi != null) nodeSettings.Calibration.TxRefRssi = (int?)Math.Round(result.TxRefRssi.Value);
            await _nsd.Set(id, nodeSettings);
        }
    }
}
