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
            var absorption = run % 10 < 9;
            Log.Information("Optimizing {0}", absorption ? "absorption" : "rx adj rssi");

            foreach (var (id, node) in _state.Nodes)
            {
                var ns = _nsd.Get(id);
                var a = absorption ? _absorption.Optimize(node, ns) : _rxAdjRssi.Optimize(node, ns);
                if (a == null) continue;
                if (a.Value.error > 10)
                {
                    Console.WriteLine($"Bad optimization {node.Id,-32} to Absorption: {a.Value.absorption:0.00} RxAdj: {a.Value.rxAdjRssi:00} Error: {a.Value.error}");
                    continue;
                }
                ns.RxAdjRssi += (int)a.Value.rxAdjRssi;
                ns.Absorption = a.Value.absorption;
                await _nsd.Set(id, ns);
                Console.WriteLine($"Optimized {node.Id,-32} to Absorption: {a.Value.absorption:0.00} RxAdj: {a.Value.rxAdjRssi:00} Error: {a.Value.error}");
            }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            run++;
        }
    }
}