using ConcurrentCollections;
using ESPresense.Locators;
using ESPresense.Models;
using ESPresense.Utils;
using MathNet.Spatial.Euclidean;
using MQTTnet.Extensions.ManagedClient;
using Newtonsoft.Json;
using Serilog;

namespace ESPresense.Optimizers;

internal class OptimizationRunner : BackgroundService
{
    private readonly DatabaseFactory _databaseFactory;
    private readonly AbsorptionAndRxAdjOptimizer _optimizer;
    private readonly State _state;
    private readonly Telemetry _telemetry = new();

    private ConcurrentHashSet<Device> _dirty = new();

    public OptimizationRunner(State state, DatabaseFactory databaseFactory, AbsorptionAndRxAdjOptimizer optimizer)
    {
        _state = state;
        _databaseFactory = databaseFactory;
        _optimizer = optimizer;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var (id, node) in _state.Nodes)
            {
                var a = _optimizer.Optimize(node);
                if (a == null) continue;
                //node.Absorption = a.Value.absorption;
                //node.RxAdj = a.Value.rxAdj;
                Console.WriteLine($"Optimized {node.Id,-32} to Absorption: {a.Value.absorption:0.00} RxAdj: {a.Value.rxAdj:00} Error: {a.Value.error}");
            }
            await Task.Delay(60000, stoppingToken);
        }
    }
}