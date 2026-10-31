using ESPresense.Events;
using ESPresense.Models;
using ESPresense.Services;
using Moq;

namespace ESPresense.Companion.Tests.Locators;

public class FingerprintCollectionTests
{
    [Test]
    public void CalibrationUsesPublishedRssiAndOnlySufficientSamplesAtKnownPositions()
    {
        var readings = new Dictionary<string, FingerprintReading>
        {
            ["near"] = new(-50, -60, 3),
            ["far"] = new(-60, -80, 3),
            ["sparse"] = new(-10, -10, 2),
            ["unplaced"] = new(-10, -10, 10)
        };
        var references = new Dictionary<string, CalibrationReference>
        {
            ["near"] = new(1, 2), ["far"] = new(10, 2), ["sparse"] = new(1, 2)
        };
        Assert.That(FingerprintCollectionService.CalculateCalibration(readings, references), Is.EqualTo(-60));
        Assert.That(FingerprintCollectionService.CalculateCalibration(readings, new Dictionary<string, CalibrationReference>()), Is.Null);
    }

    [Test]
    public async Task CollectionAveragesRawRssiAndAcceptsAliasesOnly()
    {
        var mqtt = new Mock<IMqttCoordinator>();
        var window = new TaskCompletionSource();
        var service = new FingerprintCollectionService(mqtt.Object, new ConfigLoader(Path.GetTempPath())) { Window = _ => window.Task };
        var collection = service.CollectAsync(new[] { "alias", "original" }, CancellationToken.None);
        foreach (var (device, rssi, adjustment) in new[] { ("ORIGINAL", -70.0, 5.0), ("alias", -60.0, 3.0), ("other", -10.0, 0.0) })
            mqtt.Raise(m => m.DeviceMessageReceivedAsync += null, new DeviceMessageEventArgs
            {
                DeviceId = device, NodeId = "node", Payload = new DeviceMessage { Rssi = rssi, RssiRxAdj = adjustment }
            });
        window.SetResult();
        var result = await collection;
        Assert.That(result["NODE"].RawRssi, Is.EqualTo(-61));
        Assert.That(result["NODE"].Rssi, Is.EqualTo(-65));
        Assert.That(result["NODE"].Count, Is.EqualTo(2));
        Assert.That(result.Count, Is.EqualTo(1));
    }

    [Test]
    public void CancellationStopsCollection()
    {
        var mqtt = new Mock<IMqttCoordinator>();
        var service = new FingerprintCollectionService(mqtt.Object, new ConfigLoader(Path.GetTempPath())) { Duration = Timeout.InfiniteTimeSpan };
        using var cancellation = new CancellationTokenSource();
        var collection = service.CollectAsync(new[] { "device" }, cancellation.Token);
        cancellation.Cancel();
        Assert.ThrowsAsync<TaskCanceledException>(async () => await collection);
        mqtt.VerifyRemove(m => m.DeviceMessageReceivedAsync -= It.IsAny<Func<DeviceMessageEventArgs, Task>>(), Times.Once);
    }

    [Test]
    public async Task UndoTargetsItsSampleAndPreservesLaterSamplesAndMetadata()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"fp-undo-{Guid.NewGuid()}");
        Directory.CreateDirectory(directory);
        try
        {
            var loader = new ConfigLoader(directory);
            await loader.ConfigAsync();
            var mqtt = new Mock<IMqttCoordinator>();
            var collection = new FingerprintCollectionService(mqtt.Object, loader);
            Assert.That(collection.Duration, Is.EqualTo(TimeSpan.FromSeconds(15)));
            var config = new FingerprintConfig();
            var path = Path.Combine(directory, config.File);
            File.WriteAllText(path, """{"fingerprints":[{"floor":"first","x":0,"y":0,"rssi":{}}],"survey":"manual"}""");
            var sample = new Fingerprint { Id = Guid.NewGuid(), DeviceId = "original", Floor = "first", X = 1, Rssi = new() { ["a"] = -60 } };
            collection.Save(sample, config);
            collection.Save(new Fingerprint { Id = Guid.NewGuid(), DeviceId = "other", Floor = "first", X = 9 }, config);
            var map = new FingerprintMap(path);
            var before = map.Surface("first", 0.6, -100);
            var state = new State(loader, new NodeTelemetryStore(mqtt.Object));
            state.Config = new Config { Locators = new ConfigLocators { Fingerprint = config } };
            var settings = new Mock<DeviceSettingsStore>(mqtt.Object, state);
            settings.Setup(s => s.Get("alias")).Returns(new DeviceSettings { Id = "alias", OriginalId = "original" });
            var nodeSettings = new NodeSettingsStore(mqtt.Object, Microsoft.Extensions.Logging.Abstractions.NullLogger<NodeSettingsStore>.Instance);
            var controller = new ESPresense.Controllers.FingerprintController(state, settings.Object, collection, nodeSettings);
            Assert.That(controller.Remove("unrelated", sample.Id.Value), Is.TypeOf<Microsoft.AspNetCore.Mvc.NotFoundObjectResult>());
            Assert.That(controller.Remove("alias", sample.Id.Value), Is.TypeOf<Microsoft.AspNetCore.Mvc.NoContentResult>());
            Assert.That(controller.Remove("alias", sample.Id.Value), Is.TypeOf<Microsoft.AspNetCore.Mvc.NotFoundObjectResult>());
            Assert.That(map.All.Select(f => f.X), Is.EqualTo(new[] { 0d, 9d }));
            Assert.That(map.Surface("first", 0.6, -100), Is.Not.SameAs(before));
            Assert.That(File.ReadAllText(path), Does.Contain("manual"));
            File.WriteAllText(path, "broken survey");
            Assert.Catch<System.Text.Json.JsonException>(() => collection.Remove(sample.Id.Value, "original", config));
            Assert.That(File.ReadAllText(path), Is.EqualTo("broken survey"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public void AppendPreservesSurveyAndInvalidatesSurface()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fingerprint-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(path, """{"fingerprints":[],"survey":"manual"}""");
            var map = new FingerprintMap(path);
            var first = new Fingerprint { Floor = "first", X = 1, Rssi = new() { ["node"] = -60 } };
            map.Append(first);
            var surface = map.Surface("first", 0.6, -100);
            map.Append(new Fingerprint { Floor = "second", X = 2 });
            Assert.That(new FingerprintMap(path).All.Count, Is.EqualTo(2));
            Assert.That(map.ForFloor("first").Single().Rssi["NODE"], Is.EqualTo(-60));
            Assert.That(map.Surface("first", 0.6, -100), Is.Not.SameAs(surface));
            Assert.That(File.ReadAllText(path), Does.Contain("manual"));
            File.WriteAllText(path, "{}");
            Assert.Throws<InvalidDataException>(() => map.Append(first));
            Assert.That(File.ReadAllText(path), Is.EqualTo("{}"));
            File.WriteAllText(path, "broken survey");
            Assert.Catch<System.Text.Json.JsonException>(() => map.Append(first));
            Assert.That(File.ReadAllText(path), Is.EqualTo("broken survey"));
        }
        finally { File.Delete(path); }
    }
}
