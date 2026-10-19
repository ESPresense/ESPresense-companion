using System.Text.Json;
using MQTTnet;
using MQTTnet.Packets;
using ESPresense.Events;
using ESPresense.Models;
using ESPresense.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ESPresense.Companion.Tests.Services;

public class RoomLearningTests
{
    private string _directory = null!;
    private Mock<IMqttCoordinator> _mqtt = null!;
    private Mock<DeviceSettingsStore> _settings = null!;
    private State _state = null!;
    private Device _device = null!;
    private RoomLearningService _service = null!;
    private TestClock _clock = null!;
    private readonly List<(string Topic, string? Payload, bool Retain)> _published = new();
    private string FilePath => Path.Combine(_directory, "room-fingerprints.json");
    private string Command => RoomLearningService.StateTopic(_device.Id) + "/set";
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Time = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Time;
    }

    [SetUp]
    public async Task Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "room-learning-" + Guid.NewGuid());
        Directory.CreateDirectory(_directory);
        _mqtt = new Mock<IMqttCoordinator>();
        _mqtt.SetupGet(m => m.DiscoveryTopic).Returns("custom-ha");
        _published.Clear();
        _mqtt.Setup(m => m.TryEnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .Callback<string, string?, bool>((t, p, r) => _published.Add((t, p, r))).ReturnsAsync(true);
        _mqtt.Setup(m => m.EnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .Callback<string, string?, bool>((t, p, r) => _published.Add((t, p, r))).Returns(Task.CompletedTask);
        var loader = new ConfigLoader(_directory);
        await loader.ConfigAsync();
        _state = new State(loader, new NodeTelemetryStore(_mqtt.Object));
        _state.Config = new Config
        {
            RoomLearning = new RoomLearningConfig { Enabled = true },
            Floors = new[] { new ConfigFloor { Id = "first", Name = "First", Rooms = new[]
            {
                new ConfigRoom { Id = "kitchen", Name = "Kitchen", Points = new[] { new[] { 0d, 0d }, new[] { 5d, 0d }, new[] { 0d, 5d } } },
                new ConfigRoom { Id = "office", Name = "Office", Points = new[] { new[] { 5d, 5d }, new[] { 10d, 5d }, new[] { 5d, 10d } } }
            } } }
        };
        var floor = new Floor();
        floor.Update(_state.Config, _state.Config.Floors[0]);
        _state.Floors["first"] = floor;
        _device = new Device("phone", null, TimeSpan.FromSeconds(30)) { Track = true };
        _state.Devices[_device.Id] = _device;
        _settings = new Mock<DeviceSettingsStore>(_mqtt.Object, _state);
        _clock = new TestClock();
        _service = CreateService();
    }

    private RoomLearningService CreateService() => new(_mqtt.Object, _state, _settings.Object,
        new ConfigLoader(_directory), NullLogger<RoomLearningService>.Instance, _clock);

    [TearDown]
    public void Cleanup()
    {
        _service.Dispose();
        Directory.Delete(_directory, true);
    }

    private void Hear(double a = -50, double b = -70, double c = -80, string? id = null)
    {
        foreach (var (nodeId, raw) in new[] { ("a", a), ("b", b), ("c", c) })
        {
            var message = new DeviceMessage { Rssi = raw - 5, RssiRxAdj = 5 };
            var node = new Node(nodeId, NodeSourceType.Config);
            var measurement = new DeviceToNode(_device, node);
            measurement.ReadMessage(message);
            _device.Nodes[nodeId] = measurement;
            _mqtt.Raise(m => m.DeviceMessageReceivedAsync += null, new DeviceMessageEventArgs
                { DeviceId = id ?? _device.Id, NodeId = nodeId, Payload = message });
        }
    }

    [Test]
    public void EnabledFlagDeserializesAndSurvivesConfigClone()
    {
        var deserializer = new YamlDotNet.Serialization.DeserializerBuilder().Build();
        var config = deserializer.Deserialize<Config>("room_learning:\n  enabled: true\n");
        var clone = config.Clone();
        Assert.That(clone.RoomLearning.Enabled, Is.True);
        clone.RoomLearning.Enabled = false;
        Assert.That(config.RoomLearning.Enabled, Is.True);
        Assert.That(deserializer.Deserialize<Config>("{}").RoomLearning.Enabled, Is.False);
    }

    [Test]
    public async Task DefaultConfigDoesNotLearnPublishOrOverride()
    {
        _state.Config!.RoomLearning = new Config().RoomLearning;
        Assert.That(_state.Config.RoomLearning.Enabled, Is.False);
        Hear();
        _service.QueueCorrection(Command, "Kitchen", false);
        await _service.UpdateAsync(_device);
        await _service.RemoveAsync(_device, true);
        Assert.Multiple(() =>
        {
            Assert.That(_published, Is.Empty);
            Assert.That(_device.LearnedRoom, Is.Null);
            Assert.That(File.Exists(FilePath), Is.False);
        });
    }

    [Test]
    public async Task DisablingClearsOverrideAndDiscoveryButPreservesTraining()
    {
        Hear();
        _service.QueueCorrection(Command, "Kitchen", false);
        await _service.UpdateAsync(_device);
        Assert.That(_device.LearnedRoom, Is.Not.Null);
        _state.Config!.RoomLearning.Enabled = false;
        _published.Clear();
        await _service.UpdateAsync(_device);
        Assert.Multiple(() =>
        {
            Assert.That(_device.LearnedRoom, Is.Null);
            Assert.That(_published.Any(p => p.Topic == RoomLearningService.DiscoveryTopic(_mqtt.Object, _device.Id) && p.Payload == null), Is.True);
            Assert.That(File.Exists(FilePath), Is.True);
        });
        _published.Clear();
        await _service.UpdateAsync(_device);
        Assert.That(_published, Is.Empty);
    }

    [Test]
    public async Task UnknownRoomKeepsFloorInAttributesWithoutOfferingFloorAsOption()
    {
        var floor = _state.Floors["first"];
        _device.BestScenario = new Scenario(_state.Config, new FixedLocator(floor.Rooms["office"]), "Floor only") { Floor = floor };
        Hear();
        await _service.UpdateAsync(_device);
        var attributes = JsonDocument.Parse(_published.Last(p => p.Topic.EndsWith("/attributes")).Payload!);
        Assert.That(attributes.RootElement.GetProperty("floor").GetString(), Is.EqualTo("First"));
        Assert.That(_published.Any(p => p.Topic == RoomLearningService.StateTopic(_device.Id) && p.Payload == "None"), Is.True);
        var discovery = JsonDocument.Parse(_published.Single(p => p.Topic == RoomLearningService.DiscoveryTopic(_mqtt.Object, _device.Id)).Payload!);
        Assert.That(discovery.RootElement.GetProperty("options").EnumerateArray().Select(p => p.GetString()), Does.Not.Contain("First"));
    }

    [Test]
    public async Task PredictionPublishesDiscoveryWithPlainRoomNamesButNeverTeaches()
    {
        Hear();
        await _service.UpdateAsync(_device);
        using var discovery = JsonDocument.Parse(_published.Single(p => p.Topic.EndsWith("/config")).Payload!);
        var root = discovery.RootElement;
        Assert.That(root.GetProperty("options").EnumerateArray().Select(e => e.GetString()), Is.EquivalentTo(new[] { "Kitchen", "Office" }));
        Assert.That(root.GetProperty("command_topic").GetString(), Is.EqualTo(Command));
        Assert.That(root.GetProperty("retain").GetBoolean(), Is.False);
        Assert.That(root.GetProperty("optimistic").GetBoolean(), Is.False);
        Assert.That(_published.Any(p => p.Topic.StartsWith("custom-ha/select/")), Is.True);
        _service.QueueCorrection(RoomLearningService.StateTopic(_device.Id), "Kitchen", false);
        await _service.UpdateAsync(_device);
        Assert.That(File.Exists(FilePath), Is.False);
    }

    [Test]
    public async Task CorrectionLearnsRawPatternSurvivesRestartAndDoesNotInventCoordinates()
    {
        Hear();
        var queued = 0;
        _service.CorrectionRequested += _ => queued++;
        _service.QueueCorrection(Command, "Kitchen", false);
        Assert.That(queued, Is.EqualTo(1));
        Assert.That(File.Exists(FilePath), Is.False, "Only the lease-protected locator may save a correction");
        await _service.UpdateAsync(_device);
        Assert.That(_device.Room?.Id, Is.EqualTo("kitchen"));
        Assert.That(_device.Location, Is.Null);
        var saved = new RoomFingerprintStore(FilePath).ForDevice("phone").Single();
        Assert.That(saved.Rssi["a"], Is.EqualTo(-50));
        Assert.That(_published.Any(p => p.Topic == RoomLearningService.StateTopic("phone") && p.Payload == "Kitchen"), Is.True);

        _service.Dispose();
        _service = CreateService();
        _clock.Time += TimeSpan.FromSeconds(11);
        Hear(-40, -60, -70); // Different transmitter offset, same relative pattern.
        await _service.UpdateAsync(_device);
        Assert.That(_device.Room?.Id, Is.EqualTo("kitchen"));
        Assert.That(_device.LearnedRoom?.Source, Is.EqualTo("learned"));
    }

    [Test]
    public async Task DuplicateRetainedInvalidAndStaleCommandsDoNotTeach()
    {
        Hear();
        _service.QueueCorrection(Command, "Kitchen", true);
        _service.QueueCorrection(Command, "Unknown room", false);
        await _service.UpdateAsync(_device);
        Assert.That(File.Exists(FilePath), Is.False);
        _clock.Time += TimeSpan.FromSeconds(11);
        _service.QueueCorrection(Command, "Kitchen", false);
        await _service.UpdateAsync(_device);
        Assert.That(File.Exists(FilePath), Is.False);
        Hear();
        _service.QueueCorrection(Command, "Kitchen", false);
        _service.QueueCorrection(Command, "Kitchen", false);
        await _service.UpdateAsync(_device);
        _service.QueueCorrection(Command, "Kitchen", false);
        await _service.UpdateAsync(_device);
        Assert.That(new RoomFingerprintStore(FilePath).ForDevice("phone").Count, Is.EqualTo(1));
    }

    [Test]
    public async Task CorrectionHoldExpiresAndWeakMatchesFallBack()
    {
        Hear();
        _service.QueueCorrection(Command, "Kitchen", false);
        await _service.UpdateAsync(_device);
        _clock.Time += TimeSpan.FromSeconds(4);
        Hear(-80, -45, -60);
        await _service.UpdateAsync(_device);
        Assert.That(_device.Room?.Id, Is.EqualTo("kitchen"), "Briefly hold a user's correction");
        _clock.Time += TimeSpan.FromSeconds(11);
        Hear(-80, -45, -60);
        await _service.UpdateAsync(_device);
        Assert.That(_device.LearnedRoom, Is.Null);
    }

    private sealed class FixedLocator(Room room) : ESPresense.Locators.ILocate
    {
        public bool Locate(Scenario scenario)
        {
            scenario.UpdateLocation(new MathNet.Spatial.Euclidean.Point3D(8, 8, 1));
            scenario.Room = room;
            scenario.Floor = room.Floor;
            scenario.LastHit = DateTime.UtcNow;
            scenario.Confidence = 90;
            return true;
        }
    }

    [Test]
    public async Task LocatorReportsLearnedRoomAndFallsBackWithoutChangingCoordinateScenario()
    {
        var loader = new ConfigLoader(_directory);
        var mqtt = new Mock<MqttCoordinator>(loader, NullLogger<MqttCoordinator>.Instance,
            new ESPresense.Utils.MqttNetLogger(), new SupervisorConfigLoader(NullLogger<SupervisorConfigLoader>.Instance));
        mqtt.Setup(m => m.TryEnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>())).ReturnsAsync(true);
        var events = new ESPresense.Controllers.GlobalEventDispatcher();
        var tracker = new DeviceTracker(_state, _mqtt.Object, new TelemetryService(mqtt.Object), events, _settings.Object);
        var history = new DeviceHistoryStore(new SQLite.SQLiteAsyncConnection(":memory:"), loader);
        var locator = new MultiScenarioLocator(tracker, _state, mqtt.Object, events, history, Mock.Of<ILeaseService>(), _service);
        var scenario = new Scenario(_state.Config, new FixedLocator(_state.Floors["first"].Rooms["office"]), "Coordinates");
        _device.Scenarios.Add(scenario);
        Hear();
        await locator.ProcessDevice(_device);
        Assert.That(_device.ReportedState, Is.EqualTo("Office"));
        Assert.That(_published.Any(p => p.Topic == RoomLearningService.StateTopic("phone") && p.Payload == "Office"), Is.True,
            string.Join("\n", _published.Select(p => p.Topic + " = " + p.Payload)) + $" Track={_device.Track} Current={_device.Nodes.Values.Any(n => n.Current)}");
        _service.QueueCorrection(Command, "Kitchen", false);
        await locator.ProcessDevice(_device);
        Assert.That(_device.ReportedState, Is.EqualTo("Kitchen"));
        Assert.That(scenario.Room?.Name, Is.EqualTo("Office"));
        Assert.That(scenario.Location.X, Is.EqualTo(8));
        _clock.Time += TimeSpan.FromSeconds(11);
        Hear(-80, -45, -60);
        await locator.ProcessDevice(_device);
        Assert.That(_device.ReportedState, Is.EqualTo("Office"));
    }

    [Test]
    public async Task MqttCommandEventQueuesLearningButRetainedMessagesAndAnchorsDoNot()
    {
        Hear();
        var message = new MqttApplicationMessageBuilder().WithTopic(Command).WithPayload("Kitchen").WithRetainFlag().Build();
        _mqtt.Raise(m => m.MqttMessageReceivedAsync += null,
            new object[] { new MqttApplicationMessageReceivedEventArgs("test", message, new MqttPublishPacket(), null!) });
        await _service.UpdateAsync(_device);
        Assert.That(File.Exists(FilePath), Is.False);
        message.Retain = false;
        _mqtt.Raise(m => m.MqttMessageReceivedAsync += null,
            new object[] { new MqttApplicationMessageReceivedEventArgs("test", message, new MqttPublishPacket(), null!) });
        await _service.UpdateAsync(_device);
        Assert.That(_device.Room?.Id, Is.EqualTo("kitchen"));
        _device.SetAnchor(new DeviceAnchor(new MathNet.Spatial.Euclidean.Point3D(7, 7, 1),
            _state.Floors["first"], _state.Floors["first"].Rooms["office"]));
        _service.QueueCorrection(Command, "Kitchen", false);
        await _service.UpdateAsync(_device);
        Assert.That(_device.Room?.Id, Is.EqualTo("office"));
        Assert.That(_device.LearnedRoom, Is.Null);
    }

    [Test]
    public async Task OldQueuedCorrectionsAreDiscardedAndInsufficientReadingsDoNotTrain()
    {
        Hear();
        _service.QueueCorrection(Command, "Kitchen", false);
        _clock.Time += TimeSpan.FromSeconds(16);
        Hear();
        await _service.UpdateAsync(_device);
        Assert.That(File.Exists(FilePath), Is.False);
        _clock.Time += TimeSpan.FromSeconds(4);
        _mqtt.Raise(m => m.DeviceMessageReceivedAsync += null, new DeviceMessageEventArgs
        {
            DeviceId = "phone", NodeId = "a", Payload = new DeviceMessage { Rssi = -50 }
        });
        _service.QueueCorrection(Command, "Office", false);
        await _service.UpdateAsync(_device);
        Assert.That(File.Exists(FilePath), Is.False);
    }

    [Test]
    public async Task RenamedRoomKeepsTrainingAndDeletedRoomIsNotPredicted()
    {
        Hear();
        _service.QueueCorrection(Command, "Kitchen", false);
        await _service.UpdateAsync(_device);
        _state.Config!.Floors[0].Rooms![0].Name = "New kitchen";
        _state.Floors["first"].Update(_state.Config, _state.Config.Floors[0]);
        await _service.UpdateAsync(_device);
        Assert.That(_device.Room?.Name, Is.EqualTo("New kitchen"));
        _state.Config.Floors[0].Rooms = new[] { _state.Config.Floors[0].Rooms![1] };
        await _service.UpdateAsync(_device);
        Assert.That(_device.LearnedRoom, Is.Null);
    }

    [Test]
    public async Task AliasesShareStableProfileAndDeletionClearsSamplesAndDiscovery()
    {
        _settings.Setup(s => s.Get(It.IsAny<string>())).Returns(new DeviceSettings { Id = "phone", OriginalId = "original" });
        Hear(id: "original");
        _service.QueueCorrection(Command, "Kitchen", false);
        await _service.UpdateAsync(_device);
        Assert.That(new RoomFingerprintStore(FilePath).ForDevice("original").Count, Is.EqualTo(1));
        await _service.RemoveAsync(_device, true);
        Assert.That(new RoomFingerprintStore(FilePath).ForDevice("original"), Is.Empty);
        Assert.That(_published.Any(p => p.Topic == RoomLearningService.DiscoveryTopic(_mqtt.Object, "phone") && p.Payload == null && p.Retain), Is.True);
    }

    [Test]
    public async Task CorruptTrainingFileIsNeverOverwritten()
    {
        File.WriteAllText(FilePath, "broken");
        Hear();
        _service.QueueCorrection(Command, "Kitchen", false);
        await _service.UpdateAsync(_device);
        Assert.That(File.ReadAllText(FilePath), Is.EqualTo("broken"));
        Assert.That(_device.LearnedRoom, Is.Null);
    }

    [Test]
    public void ClassifierRejectsAmbiguousOrInsufficientEvidenceAndCorrectsMistakenLabels()
    {
        var rssi = new Dictionary<string, double> { ["a"] = -50, ["b"] = -70, ["c"] = -80 };
        var kitchen = new RoomFingerprint("phone", "first", "kitchen", DateTime.UtcNow, rssi);
        var office = kitchen with { Room = "office" };
        Assert.That(RoomFingerprintStore.Predict(rssi, new[] { kitchen, office }), Is.Null);
        Assert.That(RoomFingerprintStore.Predict(new Dictionary<string, double> { ["a"] = -50, ["b"] = -70 }, new[] { kitchen }), Is.Null);
        var store = new RoomFingerprintStore(FilePath);
        store.Add(kitchen);
        store.Add(office);
        Assert.That(store.ForDevice("phone").Single().Room, Is.EqualTo("office"));
        Assert.That(store.ForDevice("another-phone"), Is.Empty);
    }
}
