using ConcurrentCollections;
using ESPresense.Locators;
using ESPresense.Models;
using ESPresense.Services;

namespace ESPresense.Optimizers;

internal class OptimizationRunner : BackgroundService
{
    private readonly DatabaseFactory _databaseFactory;
    private readonly NodeSettingsStore _nsd;
    private readonly AbsorptionAndRxAdjOptimizer _absorptionAndRxAdjOptimizer;
    private readonly TxAdjOptimizer _tx;
    private readonly State _state;
    private readonly Telemetry _telemetry = new();

    private ConcurrentHashSet<Device> _dirty = new();
    private readonly RxAdjOptimizer _rx;

    public OptimizationRunner(State state, DatabaseFactory databaseFactory, NodeSettingsStore nsd)
    {
        _state = state;
        _databaseFactory = databaseFactory;
        _nsd = nsd;
        _absorptionAndRxAdjOptimizer = new AbsorptionAndRxAdjOptimizer(_state);
        _tx = new TxAdjOptimizer(_state);
        _rx = new RxAdjOptimizer(_state);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {


        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var (id, node) in _state.Nodes)
            {
                //var a= _tx.Optimize(node);
                var ns = _nsd.Get(id);
                var a = _rx.Optimize(node, ns);
                if (a == null) continue;
                //node.Absorption = a.Value.absorption;
                //node.RxAdj = a.Value.rxAdj;
                ns.RxAdjRssi += (int)a.Value.rxAdj;
                await _nsd.Set(id, ns);

                Console.WriteLine($"Optimized {node.Id,-32} to Absorption: {a.Value.absorption:0.00} RxAdj: {a.Value.rxAdj:00} Error: {a.Value.error}");
            }
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}