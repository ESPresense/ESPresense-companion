using ConcurrentCollections;
using ESPresense.Models;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;
using MathNet.Spatial.Euclidean;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using Serilog;
using SQLite;

namespace ESPresense.Services;

internal class Multilateralizer : BackgroundService
{
    private readonly Task<IManagedMqttClient> _mc;
    private readonly ILogger<Multilateralizer> _logger;
    private readonly State _state;

    private ConcurrentHashSet<Device> _dirty = new();

    public Multilateralizer(Task<IManagedMqttClient> mc,ConfigLoader cfg, ILogger<Multilateralizer> logger, State state)
    {
        _mc = mc;
        _logger = logger;
        _state = state;

        cfg.ConfigChanged += (_, args) => { LoadConfig(args); };
        LoadConfig(cfg.Config);
    }

    private void LoadConfig(Config? c)
    {
        if (c == null) return;
        var configNodes = c.Nodes;
        if (configNodes == null) return;
        foreach (var node in configNodes)
        {
            var n2 = _state.Nodes.GetOrAdd(node.GetId(), a => new Node(c, node));
            n2.X = node.Point[0];
            n2.Y = node.Point[1];
            n2.Z = node.Point[2];
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var mc = await _mc;

        mc.ConnectingFailedAsync += (s)=>
        {
            Log.Error("MQTT connection failed {@error}: {@inner}", s.Exception.Message, s.Exception?.InnerException.Message);
            return Task.CompletedTask;
        };

        await mc.EnqueueAsync("espresense/companion/status", "online");

        await mc.SubscribeAsync("espresense/devices/#");

        mc.ApplicationMessageReceivedAsync += arg =>
        {
            var parts = arg.ApplicationMessage.Topic.Split('/');

            var deviceId = parts[2];
            var nodeId = parts[3];

            if (_state.Nodes.TryGetValue(nodeId, out var node))
            {
                var device = _state.Devices.GetOrAdd(deviceId, a => new Device { Id = a });
                if (device.Nodes.GetOrAdd(nodeId, new DeviceNode { Device = device, Node = node }).ReadMessage(arg.ApplicationMessage.Payload))
                    _dirty.Add(device);
            }

            return Task.CompletedTask;
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            while (_dirty.IsEmpty)
                await Task.Delay(1000, stoppingToken);

            var todo = _dirty;
            _dirty = new ConcurrentHashSet<Device>();


            foreach (var device in todo.AsParallel().Where(Locate))
            {
                await mc.EnqueueAsync("espresense/ips/" + device.Id, $"{{ \"x\":{device.Location.X}, \"y\":{device.Location.Y}, \"z\":{device.Location.Z}, \"name\":\"{device.Name ?? device.Id}\" }}");
                device.ReportedLocation = device.Location;
            }
        }

        // Returns: true if moved
        bool Locate(Device device)
        {
            try
            {
                var solver = new NelderMeadSimplex(1e-7, 10000);
                var obj = ObjectiveFunction.Value(x => { return Math.Pow(100, Math.Abs(1 - x[3])) + device.Nodes.Values.Where(a => a.Current).Sum(dn => Math.Pow(new Point3D(x[0], x[1], x[2]).DistanceTo(dn.Node!.Location) - x[3] * dn.Distance, 2)); });
                var prevLoc = device.Location;
                var init = Vector<double>.Build.Dense(new double[4]
                {
                    prevLoc.X,
                    prevLoc.Y,
                    prevLoc.Z,
                    device.Scale
                });
                var result = solver.FindMinimum(obj, init);

                device.Location = new Point3D(result.MinimizingPoint[0], result.MinimizingPoint[1], result.MinimizingPoint[2]);
                device.Scale = result.MinimizingPoint[3];
                var moved = Math.Abs(device.Location.DistanceTo(device.ReportedLocation)) > 0.5;

                if (moved) _logger.LogDebug("New location {0}, {1}@{2} {3} {4}", device, device.Location, device.Scale, result.FunctionInfoAtMinimum.Value, result.Iterations);
                return moved;

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error finding location for {0}", device);
                return false;
            }
        }
    }

    private Task Mc_ConnectingFailedAsync(ConnectingFailedEventArgs arg)
    {
        throw new NotImplementedException();
    }
}