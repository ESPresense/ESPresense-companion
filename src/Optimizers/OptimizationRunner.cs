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
                _state.OptimizerState.Optimizers = "Starting...";
                await Task.Delay(TimeSpan.FromSeconds(optimization.IntervalSecs), stoppingToken);

                for (int i = 0; i < 3; i++)
                {
                    _state.TakeOptimizationSnapshot();
                    await Task.Delay(TimeSpan.FromSeconds(optimization.IntervalSecs), stoppingToken);
                }

                Log.Information("Optimization cycle started");
                double previousBestCorr = double.NaN;
                double previousBestRmse = double.NaN;

                while (lease.HasLease())
                {
                    optimization = _state.Config?.Optimization;
                    if (optimization is not { Enabled: true }) break;
                    var os = _state.TakeOptimizationSnapshot();

                    var baselineResults = new OptimizationResults();
                    var (bestCorr, bestRmse) = baselineResults.Evaluate(_state.OptimizationSnaphots, _nsd);
                    // Use weights from ConfigOptimization
                    double correlationWeight = optimization.CorrelationWeight;
                    double rmseWeight = optimization.RmseWeight;
                    double bestScore = (bestCorr * correlationWeight) + ((1 - bestRmse / (1 + bestRmse)) * rmseWeight);

                    if (double.IsNaN(previousBestCorr) || double.IsNaN(previousBestRmse) || Math.Abs(bestCorr - previousBestCorr) > 0.001 || Math.Abs(bestRmse - previousBestRmse) > 0.001)
                    {
                        Log.Information("Baseline metrics: Composite={2:0.000} (R={0:0.000}, RMSE={1:0.000})", bestCorr, bestRmse, bestScore);
                        previousBestCorr = bestCorr;
                        previousBestRmse = bestRmse;
                    }

                    _state.OptimizerState.BestR = bestCorr;
                    _state.OptimizerState.BestRMSE = bestRmse;

                    IList<IOptimizer> currentOptimizers;
                    lock (_optimizersLock)
                        currentOptimizers = _optimizers.ToList();
                    _state.OptimizerState.Optimizers = string.Join(", ", currentOptimizers.Select(o => o.Name));

                    var currentSettings = os.GetNodeIds().ToDictionary(id => id, _nsd.Get);

                    foreach (var optimizer in currentOptimizers)
                    {
                        var results = optimizer.Optimize(os, currentSettings);
                        var (corr, rmse) = results.Evaluate(_state.OptimizationSnaphots, _nsd);
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

                        Log.Information("Optimizer {0,-24} found better results: Composite={1:0.000} > Best={2:0.000} (R={3:0.000}, RMSE={4:0.000})",
                            optimizer.Name, composite, bestScore, corr, rmse);
                        _state.OptimizerState.BestR = corr;
                        _state.OptimizerState.BestRMSE = rmse;

                        foreach (var (id, result) in results.Nodes)
                        {
                            Log.Information("Applied {0,-20}: Absorption={1:0.00}, RxAdj={2:00}, TxAdj={3:00}, Error={4}",
                                id, result.Absorption, result.RxAdjRssi, result.TxRefRssi, result.Error);

                            WarnIfRailed(id, result, optimization);

                            var nodeSettings = _nsd.Get(id);
                            if (result.Absorption != null) nodeSettings.Calibration.Absorption = result.Absorption;
                            if (result.RxAdjRssi != null) nodeSettings.Calibration.RxAdjRssi = (int?)Math.Round(result.RxAdjRssi.Value);
                            if (result.TxRefRssi != null) nodeSettings.Calibration.TxRefRssi = (int?)Math.Round(result.TxRefRssi.Value);
                            await _nsd.Set(id, nodeSettings);
                        }

                        bestScore = composite;
                    }

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

    // A value that converges exactly onto a configured min/max bound means the optimizer
    // wanted to go further but was clamped — the calibration is being artificially pinned
    // (the "all nodes stuck at the same absorption" degeneracy). Surface it so users know to
    // widen the relevant limit rather than silently trusting a railed value.
    private static void WarnIfRailed(string id, ProposedValues result, ConfigOptimization optimization)
    {
        const double eps = 1e-3;
        var railed = new List<string>();

        if (AtBound(result.Absorption, optimization.AbsorptionMin, optimization.AbsorptionMax, eps, out var absEdge))
            railed.Add($"Absorption={result.Absorption:0.00} ({absEdge} {(absEdge == "min" ? optimization.AbsorptionMin : optimization.AbsorptionMax):0.0})");
        if (AtBound(result.RxAdjRssi, optimization.RxAdjRssiMin, optimization.RxAdjRssiMax, eps, out var rxEdge))
            railed.Add($"RxAdjRssi={result.RxAdjRssi:0} ({rxEdge} {(rxEdge == "min" ? optimization.RxAdjRssiMin : optimization.RxAdjRssiMax):0})");
        if (AtBound(result.TxRefRssi, optimization.TxRefRssiMin, optimization.TxRefRssiMax, eps, out var txEdge))
            railed.Add($"TxRefRssi={result.TxRefRssi:0} ({txEdge} {(txEdge == "min" ? optimization.TxRefRssiMin : optimization.TxRefRssiMax):0})");

        if (railed.Count > 0)
            Log.Warning("Node {0} railed to optimization bound(s): {1}. Consider widening the corresponding limit; a clamped value is not a true fit.",
                id, string.Join(", ", railed));
    }

    private static bool AtBound(double? value, double min, double max, double eps, out string edge)
    {
        edge = string.Empty;
        if (value == null) return false;
        if (value.Value <= min + eps) { edge = "min"; return true; }
        if (value.Value >= max - eps) { edge = "max"; return true; }
        return false;
    }
}
