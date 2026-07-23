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
                    _state.OptimizerState.Optimizers = string.Empty;
                    _state.OptimizerState.Phase = "Disabled";
                    _state.OptimizerState.Message = "Auto optimization is disabled.";
                    _state.OptimizerState.NextRunAt = null;
                    _state.OptimizerState.LeaseHolder = null;
                    _state.OptimizerState.LeaseExpiresAt = null;
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    optimization = _state.Config?.Optimization;
                }

                _state.OptimizerState.Phase = "WaitingForLease";
                _state.OptimizerState.Message = "Waiting for MQTT connection or optimization lease availability.";
                var leaseTask = _leaseService.AcquireAsync(
                    OptimizationLeaseName,
                    timeout: null,
                    cancellationToken: stoppingToken);

                while (!leaseTask.IsCompleted)
                {
                    UpdateLeaseStatus();
                    await Task.WhenAny(leaseTask, Task.Delay(TimeSpan.FromSeconds(1), stoppingToken));
                }

                await using var lease = await leaseTask
                    ?? throw new InvalidOperationException("Optimization lease acquisition returned null unexpectedly.");

                Log.Information("Optimization enabled");
                _state.OptimizerState.Optimizers = GetOptimizerNames();
                _state.OptimizerState.Phase = "Collecting";
                _state.OptimizerState.Message = "Collecting snapshots for the initial training and validation split.";
                _state.OptimizerState.LeaseHolder = "This instance";
                _state.OptimizerState.LeaseExpiresAt = _leaseService.GetStatus(OptimizationLeaseName)?.ExpiresAt;
                DateTime? lastOptimizationAt = null;

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
                        if (await RunOptimizationCycle(optimization, stoppingToken))
                        {
                            lastOptimizationAt = now;
                            _state.OptimizerState.NextRunAt = now.AddSeconds(optimization.EffectiveOptimizationIntervalSecs);
                            _state.OptimizerState.Phase = "Waiting";
                            _state.OptimizerState.Message = _state.OptimizerState.LastOutcome ?? "Validation cycle completed.";
                        }
                    }
                    else
                    {
                        _state.OptimizerState.Phase = "Waiting";
                        _state.OptimizerState.Message = $"Collecting fresh samples; next validation at {_state.OptimizerState.NextRunAt:O}.";
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
                _state.OptimizerState.Phase = "Error";
                _state.OptimizerState.Message = ex.Message;
                _state.OptimizerState.NextRunAt = null;
                Log.Error(ex, "Error in OptimizationRunner");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task<bool> RunOptimizationCycle(ConfigOptimization optimization, CancellationToken stoppingToken)
    {
        var snapshots = _state.OptimizationSnaphots.ToArray();
        if (!OptimizationDataSplit.TryCreate(snapshots, optimization.EffectiveValidationFraction, out var split) || split == null)
        {
            _state.OptimizerState.Phase = "Collecting";
            _state.OptimizerState.Message = $"Collected {snapshots.Length} of 3 required snapshots.";
            Log.Information("Optimization deferred: only {SnapshotCount} snapshots are available", snapshots.Length);
            return false;
        }

        IList<IOptimizer> currentOptimizers;
        lock (_optimizersLock)
            currentOptimizers = _optimizers.ToList();

        _state.OptimizerState.Optimizers = string.Join(", ", currentOptimizers.Select(optimizer => optimizer.Name));
        _state.OptimizerState.Phase = "Optimizing";
        _state.OptimizerState.Message = $"Training on {split.Training.Measures.Count} measurements and validating newer holdout data.";
        _state.OptimizerState.TrainingSamples = split.Training.Measures.Count;
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
            _state.OptimizerState.Phase = "Collecting";
            _state.OptimizerState.Message = "The validation set has no valid measurements; collecting more data.";
            Log.Warning("Optimization deferred: the validation set has no valid measurements");
            return false;
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
        OptimizationMetrics? closestRejectedMetrics = null;
        string? closestRejectedName = null;
        string? closestRejectedParameters = null;
        string? closestRejectedReason = null;

        foreach (var optimizer in currentOptimizers)
        {
            stoppingToken.ThrowIfCancellationRequested();

            var results = optimizer.Optimize(split.Training, currentSettings).QuantizeForApplication();
            var metrics = results.EvaluateMetrics(
                split.Validation,
                _nsd,
                optimization.EffectiveHuberDelta);
            var requiredLoss = bestMetrics.HuberLoss * (1 - optimization.EffectiveMinimumImprovement);
            var excessiveRailing = HasExcessiveAbsorptionRailing(results, optimization);

            if (!metrics.IsValid || metrics.HuberLoss >= requiredLoss || excessiveRailing)
            {
                if (metrics.IsValid && (closestRejectedMetrics == null || metrics.HuberLoss < closestRejectedMetrics.HuberLoss))
                {
                    closestRejectedMetrics = metrics;
                    closestRejectedName = optimizer.Name;
                    closestRejectedParameters = DescribeAbsorptions(results, optimization);
                    closestRejectedReason = excessiveRailing
                        ? $"too many proposed absorptions are at a configured bound (maximum {optimization.EffectiveMaxAbsorptionBoundFraction:P0})"
                        : $"holdout improvement is below {optimization.EffectiveMinimumImprovement:P0}";
                }

                Log.Information(
                    "Optimizer {Optimizer,-24} rejected: Huber={Huber:0.000}, Required<{Required:0.000}, ExcessiveRailing={ExcessiveRailing} (RMSE={RMSE:0.000}, MAE={MAE:0.000}, R={Correlation:0.000})",
                    optimizer.Name,
                    metrics.HuberLoss,
                    requiredLoss,
                    excessiveRailing,
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

        _state.OptimizerState.LastRunAt = DateTime.UtcNow;
        if (bestResults == null)
        {
            _state.OptimizerState.LastOutcome = closestRejectedMetrics == null
                ? $"No valid candidate was produced; collecting more data."
                : $"Rejected {closestRejectedName}: holdout Huber {baseline.HuberLoss:0.000} -> {closestRejectedMetrics.HuberLoss:0.000} " +
                  $"(required < {baseline.HuberLoss * (1 - optimization.EffectiveMinimumImprovement):0.000}); {closestRejectedParameters}; {closestRejectedReason}.";
            return true;
        }

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

        _state.OptimizerState.LastOutcome =
            $"Applied {bestOptimizerName} to {bestResults.Nodes.Count} nodes; holdout loss {baseline.HuberLoss:0.000} -> {bestMetrics.HuberLoss:0.000}.";

        return true;
    }

    private static string DescribeAbsorptions(OptimizationResults results, ConfigOptimization optimization)
    {
        var values = results.Nodes.Values
            .Where(result => result.Absorption.HasValue)
            .Select(result => result.Absorption!.Value)
            .ToArray();
        if (values.Length == 0) return "no absorption values proposed";

        const double epsilon = 0.005;
        var railed = values.Count(value =>
            value <= optimization.AbsorptionMin + epsilon || value >= optimization.AbsorptionMax - epsilon);
        return $"absorption {values.Min():0.00}-{values.Max():0.00}, {railed}/{values.Length} at a bound";
    }

    internal static bool HasExcessiveAbsorptionRailing(OptimizationResults results, ConfigOptimization optimization)
    {
        var values = results.Nodes.Values
            .Where(result => result.Absorption.HasValue)
            .Select(result => result.Absorption!.Value)
            .ToArray();
        if (values.Length == 0) return false;

        const double epsilon = 0.005;
        var railed = values.Count(value =>
            value <= optimization.AbsorptionMin + epsilon || value >= optimization.AbsorptionMax - epsilon);
        return (double)railed / values.Length > optimization.EffectiveMaxAbsorptionBoundFraction;
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
}
