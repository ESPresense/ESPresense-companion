using ConcurrentCollections;
using ESPresense.Locators;
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

    public OptimizationRunner(State state, DatabaseFactory databaseFactory, NodeSettingsStore nsd)
    {
        _state = state;
        _nsd = nsd;
        _absorption = new AbsorptionOptimizer(_state);
        _rxAdjRssi = new RxAdjRssiOptimizer(_state);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        int run = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var absorption = run % 2 == 0;
            Log.Information("Optimizing {0}", absorption ? "absorption" : "rx adj rssi");

            foreach (var (id, node) in _state.Nodes)
            {
                var ns = _nsd.Get(id);
                var a = absorption ? _absorption.Optimize(node, ns) : _rxAdjRssi.Optimize(node, ns);
                if (a == null) continue;
                var valueRxAdjRssi = a.Value.rxAdjRssi;
                ns.RxAdjRssi += valueRxAdjRssi >= 2 ? +1 : valueRxAdjRssi <= -2 ? -1 : 0;
                ns.Absorption += a.Value.absorption > ns.Absorption ? +0.01d : a.Value.absorption < ns.Absorption ? -0.01d : 0;
                await _nsd.Set(id, ns);
                Console.WriteLine($"Optimized {node.Id,-32} to Absorption: {ns.Absorption:0.00} RxAdj: {ns.RxAdjRssi:00} Error: {a.Value.error}");
            }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            run++;
        }
    }
}