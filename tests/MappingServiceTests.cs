using System.Collections.Concurrent;
using AutoMapper;
using ESPresense.Models;
using ESPresense.Services;
using ESPresense.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MathNet.Spatial.Euclidean;
using Microsoft.Extensions.DependencyInjection;

namespace ESPresense.Companion.Tests;

public class MappingServiceTests
{
    private static Floor MakeFloor(string id, string name)
    {
        var f = new Floor();
        f.Update(new Config(), new ConfigFloor { Id = id, Name = name, Bounds = new[] { new[] { 0.0, 0.0, 0.0 }, new[] { 10.0, 10.0, 3.0 } } });
        return f;
    }

    private static Node MakeNode(string id, string name, IEnumerable<Floor> floors)
    {
        var n = new Node(id, NodeSourceType.Config);
        n.Update(new Config(), new ConfigNode { Id = id, Name = name, Point = new[] { 1.0, 2.0, 3.0 }, Floors = floors.Select(f => f.Id!).ToArray(), Stationary = true }, floors);
        return n;
    }

    private static IMapper CreateMapper()
    {
        var cfgLoader = new ConfigLoader(Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg-ms"));
        var supervisor = new SupervisorConfigLoader(NullLogger<SupervisorConfigLoader>.Instance);
        var mqtt = new Mock<MqttCoordinator>(cfgLoader, NullLogger<MqttCoordinator>.Instance, new MqttNetLogger(), supervisor);

        var nts = new NodeTelemetryStore(mqtt.Object);
        var fs = new FirmwareTypeStore(new HttpClient());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(nts);
        services.AddSingleton(fs);
        services.AddAutoMapper(cfg => { }, typeof(MappingProfile).Assembly);
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IMapper>();
    }

    [Test]
    public void MapNodes_MapsBasicFields()
    {
        // Arrange
        var floors = new[] { MakeFloor("f1", "Floor 1"), MakeFloor("f2", "Floor 2") };
        var n1 = MakeNode("n1", "Node 1", floors);
        var n2 = MakeNode("n2", "Node 2", floors);

        // add a NodeToNode relation to verify copy
        n1.Nodes["n2"] = new NodeToNode(n1, n2) { Distance = 5.5, Rssi = -60 };

        var mapper = CreateMapper();

        // Act
        var mapped = mapper.Map<IEnumerable<NodeState>>(new[] { n1, n2 }).ToArray();

        // Assert
        Assert.That(mapped.Length, Is.EqualTo(2));
        var m1 = mapped[0];
        Assert.That(m1.Id, Is.EqualTo("n1"));
        Assert.That(m1.Name, Is.EqualTo("Node 1"));
        Assert.That(m1.Location, Is.EqualTo(new Point3D(1, 2, 3)));
        Assert.That(m1.Floors, Is.EqualTo(new[] { "f1", "f2" }));
        Assert.That(m1.Nodes.ContainsKey("n2"), Is.True);
        Assert.That(m1.Nodes["n2"].Distance, Is.EqualTo(5.5));
    }

    [Test]
    public void MapNodesWithTele_NoTelemetry_YieldsNullsAndOffline()
    {
        // Arrange
        var floors = new[] { MakeFloor("f1", "Floor 1") };
        var n1 = MakeNode("n1", "Node 1", floors);
        var mapper = CreateMapper();

        // Act
        var mapped = mapper.Map<IEnumerable<NodeStateTele>>(new[] { n1 }).Single();

        // Assert
        Assert.That(mapped.Id, Is.EqualTo("n1"));
        Assert.That(mapped.Telemetry, Is.Null);
        Assert.That(mapped.Flavor, Is.Null);
        Assert.That(mapped.CPU, Is.Null);
        Assert.That(mapped.Online, Is.False);
    }
}
