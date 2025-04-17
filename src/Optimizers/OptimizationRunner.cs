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
    private readonly State _state;
    private readonly NodeSettingsStore _nsd;
    private readonly ILogger<OptimizationRunner> _logger;
    private readonly ConfigLoader _cfg;
    private IList<IOptimizer> _optimizers;
    private readonly object _optimizersLock = new();
    private string? _lastOptimizerMode;

    public OptimizationRunner(State state, NodeSettingsStore nsd, ILogger<OptimizationRunner> logger, ConfigLoader cfg)
    {
        _state = state;
        _nsd = nsd;
        _logger = logger;
        _cfg = cfg;
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
                new AbsorptionErrOptimizer(_state)
            }
        };
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var optimization = _state.Config?.Optimization;
            while (optimization is not { Enabled: true })
            {
                Log.Information("Optimization disabled");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                optimization = _state.Config?.Optimization;
            }

            Log.Information("Optimization enabled");
            await Task.Delay(TimeSpan.FromSeconds(optimization.IntervalSecs), stoppingToken);

            for (int i = 0; i < 3; i++)
            {
                _state.TakeOptimizationSnapshot();
                await Task.Delay(TimeSpan.FromSeconds(optimization.IntervalSecs), stoppingToken);
            }

            while (optimization is { Enabled: true })
            {
                Log.Information("Optimization cycle started");
                var os = _state.TakeOptimizationSnapshot();

                var baselineResults = new OptimizationResults();
                var (bestCorr, bestRmse) = baselineResults.Evaluate(_state.OptimizationSnaphots, _nsd);
                // Use weights from ConfigOptimization
                double correlationWeight = optimization.CorrelationWeight;
                double rmseWeight = optimization.RmseWeight;
                double bestScore = (bestCorr * correlationWeight) + ((1 - bestRmse / (1 + bestRmse)) * rmseWeight);
                Log.Information("Baseline metrics: R={0:0.000}, RMSE={1:0.000}, Composite={2:0.000}", bestCorr, bestRmse, bestScore);

                IList<IOptimizer> currentOptimizers;
                lock (_optimizersLock)
                    currentOptimizers = _optimizers.ToList();

                var currentSettings = os.GetNodeIds().ToDictionary(id => id, _nsd.Get);

                foreach (var optimizer in currentOptimizers)
                {
                    var results = optimizer.Optimize(os, currentSettings);
                    var (corr, rmse) = results.Evaluate(_state.OptimizationSnaphots, _nsd);
                    // Use weights from ConfigOptimization
                    var composite = (corr * correlationWeight) + ((1 - rmse / (1 + rmse)) * rmseWeight);

                    if (double.IsNaN(composite) || double.IsInfinity(composite) || composite <= bestScore)
                    {
                        Log.Information("Optimizer {0,-24} found worse results, Composite={1:0.000} <= Best={2:0.000} (R={3:0.000}, RMSE={4:0.000})",
                            optimizer.Name, composite, bestScore, corr, rmse);

                        foreach (var (id, result) in results.Nodes)
                            Log.Debug("Rejected {0,-20}: Absorption={1:0.00}, RxAdj={2:00}, TxAdj={3:00}, Error={4}",
                                id, result.Absorption, result.RxAdjRssi, result.TxRefRssi, result.Error);
                        continue;
                    }

                    Log.Information("Optimizer {0,-24} found better results, Composite={1:0.000} > Best={2:0.000} (R={3:0.000}, RMSE={4:0.000})",
                        optimizer.Name, composite, bestScore, corr, rmse);

                    foreach (var (id, result) in results.Nodes)
                    {
                        Log.Information("Applied {0,-20}: Absorption={1:0.00}, RxAdj={2:00}, TxAdj={3:00}, Error={4}",
                            id, result.Absorption, result.RxAdjRssi, result.TxRefRssi, result.Error);

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
    }
}
