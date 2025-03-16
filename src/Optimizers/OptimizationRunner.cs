using ESPresense.Models;
using ESPresense.Services;
using Serilog;

namespace ESPresense.Optimizers;

internal class OptimizationRunner : BackgroundService
{
    private readonly State _state;
    private readonly NodeSettingsStore _nsd;
    private readonly ILogger<OptimizationRunner> _logger;
    private readonly IList<IOptimizer> _optimizers;

    public OptimizationRunner(State state, NodeSettingsStore nsd, ILogger<OptimizationRunner> logger)
    {
        _state = state;
        _nsd = nsd;
        _logger = logger;
        _optimizers = new List<IOptimizer> { new RxAdjRssiOptimizer(_state), new AbsorptionAvgOptimizer(_state), new AbsorptionErrOptimizer(_state) };
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

                foreach (var optimizer in _optimizers)
                {
                    var results = optimizer.Optimize(os);
                    var d = results.Evaluate(_state.OptimizationSnaphots, _nsd);
                    if (d >= best || double.IsNaN(d) || double.IsInfinity(d))
                    {
                        Log.Information("Optimizer {0,-24} found worse  results, rms {1:0.000}>{2:0.000}", optimizer.Name, d, best);
                        continue;
                    }
                    Log.Information("Optimizer {0,-24} found better results, rms {1:0.000}<{2:0.000}", optimizer.Name, d, best);
                    foreach (var (id, result) in results.RxNodes)
                    {
                        Log.Information("Optimizer set {0,-20} to Absorption: {1:0.00} RxAdj: {2:00} Error: {3}", id, result.Absorption, result.RxAdjRssi, result.Error);
                        var a = _nsd.Get(id);
                        if (optimization == null) continue;
                        if (result.Absorption != null && result.Absorption > optimization.AbsorptionMin && result.Absorption < optimization.AbsorptionMax) a.Calibration.Absorption = result.Absorption;
                        if (result.RxAdjRssi != null && result.RxAdjRssi > optimization.RxAdjRssiMin && result.RxAdjRssi < optimization.RxAdjRssiMax) a.Calibration.RxAdjRssi = result.RxAdjRssi == null ? 0 : (int?)Math.Round(result.RxAdjRssi.Value);
                        await _nsd.Set(id, a);
                    }

                    best = d;
                }

                await Task.Delay(TimeSpan.FromSeconds(optimization?.IntervalSecs ?? 60), stoppingToken);
            }
        }

    }
}