using ConcurrentCollections;
using ESPresense.Models;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;
using MathNet.Spatial.Euclidean;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using SQLite;

namespace ESPresense;

internal class Multilateralizer : BackgroundService
{
    private readonly Config _cfg;
    private readonly ILogger<Multilateralizer> _logger;
    private readonly State _state;

    private ConcurrentHashSet<Device> dirty = new();

    
    public Multilateralizer(Config cfg, ILogger<Multilateralizer> logger , State state)
    {
        _cfg = cfg;
        _logger = logger;
        _state = state;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var node in _cfg.Nodes)
            _state.Nodes.TryAdd(node.GetId(), new Node(node));
        var mqttFactory = new MqttFactory();

        var mc = mqttFactory.CreateManagedMqttClient();
        var mqttClientOptions = new MqttClientOptionsBuilder()
            .WithTcpServer("mqtt")
            .WithWillTopic("espresense/companion/status")
            .WithWillRetain()
            .WithWillPayload("offline")
            .Build();
        var managedMqttClientOptions = new ManagedMqttClientOptionsBuilder()
            .WithClientOptions(mqttClientOptions)
            .Build();

        await mc.StartAsync(managedMqttClientOptions);

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
                    dirty.Add(device);
            }

            return Task.CompletedTask;
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            while (dirty.IsEmpty)
                await Task.Delay(1000, stoppingToken);

            var todo = dirty;
            dirty = new ConcurrentHashSet<Device>();


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
                var obj = ObjectiveFunction.Value(x => { return Math.Pow(100, Math.Abs(1 - x[3])) + device.Nodes.Values.OrderByDescending(a => a.Distance).Take(5).Sum(dn => Math.Pow(new Point3D(x[0], x[1], x[2]).DistanceTo(dn.Node!.Location) - (x[3] * dn.Distance), 2)); });
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

                if (moved) _logger.LogInformation("New location {0}, {1}@{2} {3} {4}", device, device.Location, device.Scale, result.FunctionInfoAtMinimum.Value, result.Iterations);
                return moved;

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error finding location for {0}", device);
                return false;
            }
        }
    }
}