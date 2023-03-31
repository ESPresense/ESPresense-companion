using ESPresense.Models;
using ESPresense.Services;
using Serilog;

namespace ESPresense.Optimizers;

internal class OptimizationRunner : BackgroundService
{
    private readonly NodeSettingsStore _nsd;
    private readonly State _state;
    private readonly RxAdjRssiOptimizer _rxAdjRssi;
    private readonly AbsorptionOptimizer _absorption;

    public OptimizationRunner(State state, NodeSettingsStore nsd)
    {
        _state = state;
        _nsd = nsd;
        _absorption = new AbsorptionOptimizer();
        _rxAdjRssi = new RxAdjRssiOptimizer();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        int run = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var absorption = run % 2 == 0;
            Log.Information("Optimizing {0}", absorption ? "absorption" : "rx adj rssi");

            var os = _state.TakeOptimizationSnapshot();
            var optimizationResults = absorption ? _absorption.Optimize(os) : _rxAdjRssi.Optimize(os);
            foreach (var result in optimizationResults)
            {
                var ns = _nsd.Get(result.NodeId!);
                if (optimizationResults == null) continue;
                var valueRxAdjRssi = result.RxAdjRssi;
                ns.RxAdjRssi += valueRxAdjRssi >= 2 ? +1 : valueRxAdjRssi <= -2 ? -1 : 0;
                ns.Absorption += result.Absorption > ns.Absorption ? +0.01d : result.Absorption < ns.Absorption ? -0.01d : 0;
                //await _nsd.Set(id, ns);
                Console.WriteLine($"Optimizer found {result.NodeId,-32} to Absorption: {result.Absorption:0.00} RxAdj: {result.RxAdjRssi:00} Error: {result.Error}");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            run++;
        }
    }
}