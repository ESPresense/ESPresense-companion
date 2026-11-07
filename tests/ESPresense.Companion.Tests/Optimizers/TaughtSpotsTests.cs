using ESPresense.Optimizers;
using MathNet.Spatial.Euclidean;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace ESPresense.Companion.Tests.Optimizers;

/// <summary>Taught locations (fingerprints.json) as spots for the spot score and judge (#1868). All ids are made up.</summary>
public class TaughtSpotsTests
{
    private string _dir = null!;
    private string Path => System.IO.Path.Combine(_dir, "fingerprints.json");

    [SetUp]
    public void Setup()
    {
        _dir = System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "taught", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static readonly Dictionary<string, Point3D> Placed = new()
    {
        ["rx-a"] = new Point3D(0, 0, 1), ["rx-b"] = new Point3D(6, 0, 1), ["rx-c"] = new Point3D(0, 5, 1),
    };

    /// <summary>rx-c serves only the upper floor; the rest list no floors, so count on every floor.</summary>
    private static (Point3D, IReadOnlyCollection<string>)? At(string id) =>
        Placed.TryGetValue(id, out var p) ? (p, id == "rx-c" ? new[] { "f2" } : Array.Empty<string>()) : null;

    [Test]
    public void RepeatSamplesAtOnePlace_AreOneSpot_AndUnplacedReceiversAreLeftOut()
    {
        File.WriteAllText(Path, """
            {"fingerprints": [
              {"floor": "f2", "x": 2.0, "y": 2.0, "z": 0.3, "rssi": {"rx-a": -60, "rx-b": -70, "rx-c": -66, "rx-unplaced": -50}},
              {"floor": "f2", "x": 2.1, "y": 1.95, "z": 0.3, "rssi": {"rx-a": -62, "rx-b": -72, "rx-c": -64}},
              {"floor": "f2", "x": 4.0, "y": 3.0, "rssi": {"rx-a": -70, "rx-b": -65, "rx-c": -68}},
              {"floor": "f2", "x": 5.0, "y": 4.0, "rssi": {"rx-a": -70, "rx-unplaced": -60}}
            ]}
            """);

        var spots = new TaughtSpots(Path).Read(At);

        Assert.That(spots, Has.Count.EqualTo(2), "two samples within the grid merge; the last has one placed receiver");
        var merged = spots.Single(s => s.Location.X < 3);
        Assert.That(merged.Readings.ToDictionary(r => r.RxId, r => r.RawRssi),
            Is.EqualTo(new Dictionary<string, double> { ["rx-a"] = -61, ["rx-b"] = -71, ["rx-c"] = -65 }));
        Assert.That(merged.Location.X, Is.EqualTo(2.05).Within(1e-9));
        Assert.That(merged.Readings.Single(r => r.RxId == "rx-b").RxLocation, Is.EqualTo(Placed["rx-b"]));
    }

    [Test]
    public void AReceiverOnAnotherFloor_DoesNotCount()
    {
        // rx-c serves f2 only: on an f1 spot it is heard through the floor, which says nothing about its calibration.
        File.WriteAllText(Path, """
            {"fingerprints": [
              {"floor": "f1", "x": 2, "y": 2, "rssi": {"rx-a": -60, "rx-b": -70, "rx-c": -66}},
              {"floor": "f2", "x": 2, "y": 2, "rssi": {"rx-a": -61, "rx-b": -71, "rx-c": -64}}
            ]}
            """);

        var spots = new TaughtSpots(Path).Read(At);

        Assert.That(spots.Single().Id, Does.Contain("f2"), "the f1 spot is left with two receivers, too few");
    }

    [Test]
    public void MissingFile_IsNoSpots()
    {
        Assert.That(new TaughtSpots(Path).Read(At), Is.Empty);
    }

    [Test]
    public void MalformedFile_IsNoSpots_AndWarnsOnce()
    {
        File.WriteAllText(Path, "{ not json");
        var log = new FakeLogger<TaughtSpotsTests>();
        var taught = new TaughtSpots(Path, log);

        Assert.That(taught.Read(At), Is.Empty);
        Assert.That(taught.Read(At), Is.Empty);
        Assert.That(log.Collector.GetSnapshot().Count(r => r.Level == LogLevel.Warning), Is.EqualTo(1));
    }

    [Test]
    public void AnEditedFile_IsReadAgain()
    {
        var taught = new TaughtSpots(Path);
        File.WriteAllText(Path, """{"fingerprints": []}""");
        Assert.That(taught.Read(At), Is.Empty);

        File.WriteAllText(Path, """{"fingerprints": [{"floor": "f2", "x": 1, "y": 1, "rssi": {"rx-a": -60, "rx-b": -70, "rx-c": -66}}]}""");
        File.SetLastWriteTimeUtc(Path, DateTime.UtcNow.AddSeconds(5));

        Assert.That(taught.Read(At), Has.Count.EqualTo(1));
    }
}
