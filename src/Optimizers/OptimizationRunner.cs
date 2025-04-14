using ESPresense.Models;
using ESPresense.Services;
using Serilog;

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
        _lastOptimizerMode = null;

        // Subscribe to config changes for live optimizer updates
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

        // Initialize optimizers from current config
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
        double best;

        // Removed the reEvaluate task since we're now recalculating the baseline
        // at each optimization cycle with the current snapshots

        while (!stoppingToken.IsCancellationRequested)
        {
            var optimization = _state.Config?.Optimization;
            int run = 0;
            while (optimization is not { Enabled: true })
            {
                if (run++ == 0) Log.Information("Optimization disabled");
                optimization = _state.Config?.Optimization;
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }

            Log.Information("Optimization enabled");
            await Task.Delay(TimeSpan.FromSeconds(optimization.IntervalSecs), stoppingToken);

            for (int i = 0; i < 3; i++)
            {
                _state.TakeOptimizationSnapshot();
                await Task.Delay(TimeSpan.FromSeconds(optimization?.IntervalSecs ?? 60), stoppingToken);
            }

            run = 0;
            while (optimization is { Enabled: true })
            {
                if (run++ == 0) Log.Information("Optimization started");
                var os = _state.TakeOptimizationSnapshot();

                best = new OptimizationResults().Evaluate(_state.OptimizationSnaphots, _nsd);

                IList<IOptimizer> currentOptimizers;
                lock (_optimizersLock)
                {
                    currentOptimizers = _optimizers.ToList();
                }

                var currentSettings = os.GetNodeIds().ToDictionary(id => id, _nsd.Get);
                foreach (var optimizer in currentOptimizers)
                {
                    var results = optimizer.Optimize(os, currentSettings);
                    var d = results.Evaluate(_state.OptimizationSnaphots, _nsd);
                    if (d <= best || double.IsNaN(d) || double.IsInfinity(d))
                    {
                        Log.Information("Optimizer {0,-24} found worse results, r={1:0.000}<{2:0.000}", optimizer.Name, d, best);
                        foreach (var (id, result) in results.Nodes)
                            Log.Debug("Optimizer wanted {0,-20} to Absorption: {1:0.00} RxAdj: {2:00} TxAdj: {3:00} Error: {4}", id, result.Absorption, result.RxAdjRssi, result.TxRefRssi, result.Error);
                        continue;
                    }
                    Log.Information("Optimizer {0,-24} found better results, r={1:0.000}>{2:0.000}", optimizer.Name, d, best);
                    foreach (var (id, result) in results.Nodes)
                    {
                        Log.Information("Optimizer set {0,-20} to Absorption: {1:0.00} RxAdj: {2:00} TxAdj: {3:00} Error: {4}", id, result.Absorption, result.RxAdjRssi, result.TxRefRssi, result.Error);
                        var a = _nsd.Get(id);
                        if (optimization == null) continue;
                        if (result.Absorption != null) a.Calibration.Absorption = result.Absorption;
                        if (result.RxAdjRssi != null) a.Calibration.RxAdjRssi = result.RxAdjRssi == null ? 0 : (int?)Math.Round(result.RxAdjRssi.Value);
                        if (result.TxRefRssi != null) a.Calibration.TxRefRssi = result.TxRefRssi == null ? 0 : (int?)Math.Round(result.TxRefRssi.Value);
                        await _nsd.Set(id, a);
                    }

                    best = d;
                }

                await Task.Delay(TimeSpan.FromSeconds(optimization?.IntervalSecs ?? 60), stoppingToken);
            }
        }

    }
}