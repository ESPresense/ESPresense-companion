using ESPresense.Models;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Moq;

namespace ESPresense.Companion.Tests.Services;

public class DeviceStatePublisherTests
{
    private const string StateTopic = "espresense/companion/test:pub";
    private const string AttributesTopic = "espresense/companion/test:pub/attributes";

    private Mock<IMqttCoordinator> _mqtt = null!;
    private List<(string Topic, string? Payload)> _sent = null!;
    private Queue<bool> _results = null!;
    private DeviceStatePublisher _publisher = null!;
    private Device _device = null!;

    [SetUp]
    public void SetUp()
    {
        _sent = new();
        _results = new();
        _mqtt = new Mock<IMqttCoordinator>();
        _mqtt.Setup(m => m.TryEnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), true))
            .Callback<string, string?, bool>((t, p, _) => _sent.Add((t, p)))
            .ReturnsAsync(() => _results.Count == 0 || _results.Dequeue());
        _publisher = new DeviceStatePublisher(_mqtt.Object);
        _device = new Device("test:pub", null, TimeSpan.FromSeconds(30));
    }

    private static DeviceReport Located(string state, double x = 1) =>
        new(state, new Point3D(x, 2, 0.5), null, null, null, 70, 3, "Floor One", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

    private static DeviceReport Away() => new(DeviceReport.NotHome, null, null, null, null, null, null, null, null);

    private string[] Topics() => _sent.Select(s => s.Topic).ToArray();

    [Test]
    public async Task FirstReport_SendsStateThenAttributes()
    {
        var published = await _publisher.PublishAsync(_device, Located("Room One"), locationChanged: false);

        Assert.That(published, Is.True);
        Assert.That(_sent, Is.EqualTo(new (string, string?)[]
        {
            (StateTopic, "Room One"),
            (AttributesTopic, "{\"source_type\":\"espresense\",\"x\":1.0,\"y\":2.0,\"z\":0.5,\"confidence\":70,\"fixes\":3,\"best_scenario\":\"Floor One\",\"last_seen\":\"2026-01-02T03:04:05Z\"}")
        }));
        Assert.That(_publisher.Reported(_device), Is.EqualTo(new ReportedDeviceState(
            "Room One", new Point3D(1, 2, 0.5), "Floor One", null, _publisher.Reported(_device).StateChanged)));
    }

    [Test]
    public async Task SameStateNotMoved_SendsNothing()
    {
        await _publisher.PublishAsync(_device, Located("Room One"), locationChanged: true);
        _sent.Clear();

        var published = await _publisher.PublishAsync(_device, Located("Room One", x: 3), locationChanged: false);

        Assert.That(published, Is.False);
        Assert.That(_sent, Is.Empty);
    }

    [Test]
    public async Task SameStateMoved_SendsAttributesOnly()
    {
        await _publisher.PublishAsync(_device, Located("Room One"), locationChanged: true);
        _sent.Clear();

        var published = await _publisher.PublishAsync(_device, Located("Room One", x: 3), locationChanged: true);

        Assert.That(published, Is.True);
        Assert.That(Topics(), Is.EqualTo(new[] { AttributesTopic }));
        Assert.That(_publisher.Reported(_device).Location, Is.EqualTo(new Point3D(3, 2, 0.5)));
    }

    [Test]
    public async Task NoLocation_SendsStateButNeverAttributes()
    {
        var published = await _publisher.PublishAsync(_device, Away(), locationChanged: true);

        Assert.That(published, Is.False);
        Assert.That(Topics(), Is.EqualTo(new[] { StateTopic }));
        Assert.That(_sent[0].Payload, Is.EqualTo("not_home"));
    }

    [Test]
    public async Task FailedState_IsRetriedNextReport()
    {
        _results.Enqueue(false); // state
        _results.Enqueue(true);  // attributes
        await _publisher.PublishAsync(_device, Located("Room One"), locationChanged: false);
        Assert.That(_publisher.Reported(_device).State, Is.Null);
        _sent.Clear();

        await _publisher.PublishAsync(_device, Located("Room One"), locationChanged: false);

        Assert.That(Topics(), Is.EqualTo(new[] { StateTopic, AttributesTopic }));
        Assert.That(_publisher.Reported(_device).State, Is.EqualTo("Room One"));
    }

    [Test]
    public async Task FailedAttributes_AreRetriedWithoutResendingState()
    {
        _results.Enqueue(true);  // state
        _results.Enqueue(false); // attributes
        var first = await _publisher.PublishAsync(_device, Located("Room One"), locationChanged: false);
        Assert.That(first, Is.False);
        Assert.That(_publisher.Reported(_device).AttributesPending, Is.True);
        _sent.Clear();

        var second = await _publisher.PublishAsync(_device, Located("Room One"), locationChanged: false);
        Assert.That(second, Is.True);
        Assert.That(Topics(), Is.EqualTo(new[] { AttributesTopic }));
        _sent.Clear();

        await _publisher.PublishAsync(_device, Located("Room One"), locationChanged: false);
        Assert.That(_sent, Is.Empty);
    }

    [Test]
    public async Task PreviousState_TracksTheLastAcceptedDifferentState()
    {
        await _publisher.PublishAsync(_device, Located("Room One"), false);
        await _publisher.PublishAsync(_device, Located("Room Two"), false);
        _results.Enqueue(false); // a failed state publish is not a change
        await _publisher.PublishAsync(_device, Located("Room Three"), false);
        await _publisher.PublishAsync(_device, Located("Room Two"), false);

        var reported = _publisher.Reported(_device);
        Assert.That(reported.State, Is.EqualTo("Room Two"));
        Assert.That(reported.PreviousState, Is.EqualTo("Room One"));
        Assert.That(reported.StateChanged, Is.Not.Null);
    }

    [Test]
    public async Task BookkeepingIsPerDeviceInstance()
    {
        // A device deleted and seen again under the same id had its retained topics cleared, so it starts over
        await _publisher.PublishAsync(_device, Located("Room One"), false);
        _sent.Clear();

        var again = new Device(_device.Id, null, TimeSpan.FromSeconds(30));
        await _publisher.PublishAsync(again, Located("Room One"), false);

        Assert.That(Topics(), Is.EqualTo(new[] { StateTopic, AttributesTopic }));
    }
}

public class DeviceHistoryWriterTests
{
    [OneTimeSetUp]
    public void InitSqlite() => SQLitePCL.Batteries.Init();

    [Test]
    public async Task RecordsOneRowPerScenarioWithConfidence_OnlyWhenEnabled()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "espresense-history-writer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        await File.WriteAllTextAsync(Path.Combine(workDir, "config.yaml"), "history:\n  enabled: true\n");
        var configLoader = new ConfigLoader(workDir);
        await configLoader.StartAsync(CancellationToken.None);
        var config = await configLoader.ConfigAsync();

        var state = new State(configLoader, new NodeTelemetryStore(new Mock<IMqttCoordinator>().Object));
        var store = new DeviceHistoryStore(new SQLite.SQLiteAsyncConnection(":memory:"), configLoader);
        var writer = new DeviceHistoryWriter(store, state);

        var device = new Device("test:hist", null, TimeSpan.FromSeconds(30));
        var a = new Scenario(null, new Mock<ESPresense.Locators.ILocate>().Object, "a") { Confidence = 50, Fixes = 3 };
        var b = new Scenario(null, new Mock<ESPresense.Locators.ILocate>().Object, "b") { Confidence = 0 };
        var c = new Scenario(null, new Mock<ESPresense.Locators.ILocate>().Object, "c") { Confidence = 20 };
        a.ResetLocation(new Point3D(1, 2, 3));
        foreach (var s in new[] { a, b, c }) device.Scenarios.Add(s);

        state.Apply(new Config()); // history off in the running config: nothing is written
        await writer.RecordAsync(device, a);
        Assert.That(await store.List(device.Id), Is.Empty);

        state.Apply(config);
        await writer.RecordAsync(device, a);

        var rows = (await store.List(device.Id))!.OrderBy(r => r.Scenario).ToList();
        Assert.That(rows.Select(r => (r.Scenario, r.Best)), Is.EqualTo(new[] { ("a", true), ("c", false) }));
        Assert.That((rows[0].X, rows[0].Y, rows[0].Z, rows[0].Confidence, rows[0].Fixes), Is.EqualTo((1.0, 2.0, 3.0, 50, 3)));
    }
}
