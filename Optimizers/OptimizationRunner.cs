using System.Diagnostics.Metrics;
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

        var reEvaluate = Task.Run(async () =>
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                best = new OptimizationResults().Evaluate(_state.OptimizationSnaphots, _nsd);
            }
        }, stoppingToken);

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

            best = new OptimizationResults().Evaluate(_state.OptimizationSnaphots, _nsd);

            run = 0;
            while (optimization is { Enabled: true })
            {
                if (run++ == 0) Log.Information("Optimization started");
                var os = _state.TakeOptimizationSnapshot();
                foreach (var optimizer in _optimizers)
                {
                    var results = optimizer.Optimize(os);
                    var d = results.Evaluate(_state.OptimizationSnaphots, _nsd);
                    if (d >= best || double.IsNaN(d) || double.IsInfinity(d))
                    {
                        Log.Information("Optimizer {0} found worse results, rms {1}>{2}", optimizer.Name, d, best);
                        continue;
                    }
                    Log.Information("Optimizer {0} found better results, rms {1}<{2}", optimizer.Name, d, best);
                    foreach (var (id, result) in results.RxNodes)
                    {
                        _logger.LogInformation($"Optimizer set {id,-32} to Absorption: {result.Absorption,5:0.00} RxAdj: {result.RxAdjRssi,3:00} Error: {result.Error}");
                        var a = _nsd.Get(id);
                        if (optimization == null) continue;
                        if (result.Absorption != null && result.Absorption > optimization.AbsorptionMin && result.Absorption < optimization.AbsorptionMax) a.Absorption = result.Absorption;
                        if (result.RxAdjRssi != null && result.RxAdjRssi > optimization.RxAdjRssiMin && result.RxAdjRssi < optimization.RxAdjRssiMax) a.RxAdjRssi = result.RxAdjRssi == null ? 0 : (int?)Math.Round(result.RxAdjRssi.Value);
                        await _nsd.Set(id, a);
                    }

                    best = d;
                }

                await Task.Delay(TimeSpan.FromSeconds(optimization?.IntervalSecs ?? 60), stoppingToken);

            }
        }

        await reEvaluate;
    }
}