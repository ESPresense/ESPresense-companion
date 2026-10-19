using ESPresense.Locators;
using ESPresense.Models;
using ESPresense.Services;
using Moq;

namespace ESPresense.Companion.Tests.Locators;

[TestFixture]
public class FingerprintLocatorTests
{
    private static Fingerprint F(double x, double y, params (string Node, double Rssi)[] r) =>
        new() { Floor = "first", X = x, Y = y, Z = 0, Rssi = r.ToDictionary(a => a.Node, a => a.Rssi, StringComparer.OrdinalIgnoreCase) };

    private static readonly Fingerprint[] Map =
    {
        F(0, 0, ("a", -50), ("b", -70), ("c", -80), ("d", -75)),
        F(5, 0, ("a", -70), ("b", -50), ("c", -75), ("d", -80)),
        F(5, 5, ("a", -80), ("b", -75), ("c", -50), ("d", -70)),
        F(0, 5, ("a", -75), ("b", -80), ("c", -70), ("d", -50)),
    };

    [Test]
    public void ExactReadingLandsOnItsFingerprint()
    {
        var m = FingerprintLocator.Match(new Dictionary<string, double> { ["a"] = -70, ["b"] = -50, ["c"] = -75, ["d"] = -80 }, Map, k: 1, minCommon: 3)!;
        Assert.That((m.Location.X, m.Location.Y), Is.EqualTo((5.0, 0.0)));
        Assert.That(m.Residual, Is.EqualTo(0).Within(1e-9));
    }

    [Test]
    public void ConstantOffsetIsIgnored()
    {
        // A device transmitting 12 dB hotter than the survey beacon still matches the same spot.
        var m = FingerprintLocator.Match(new Dictionary<string, double> { ["a"] = -68, ["b"] = -63, ["c"] = -38, ["d"] = -58 }, Map, k: 1, minCommon: 3)!;
        Assert.That((m.Location.X, m.Location.Y), Is.EqualTo((5.0, 5.0)));
        Assert.That(m.Residual, Is.EqualTo(0).Within(1e-9));
    }

    [Test]
    public void TooFewSharedNodesIsNoMatch()
    {
        Assert.That(FingerprintLocator.Match(new Dictionary<string, double> { ["a"] = -50, ["x"] = -60 }, Map, k: 3, minCommon: 3), Is.Null);
    }

    [Test]
    public void KNearestAreAveragedTowardsTheBestMatch()
    {
        // Halfway between the two left-hand spots, slightly nearer (0,0).
        var m = FingerprintLocator.Match(new Dictionary<string, double> { ["a"] = -60, ["b"] = -76, ["c"] = -76, ["d"] = -64 }, Map, k: 2, minCommon: 3)!;
        Assert.Multiple(() =>
        {
            Assert.That(m.Location.X, Is.EqualTo(0).Within(1e-9));
            Assert.That(m.Location.Y, Is.GreaterThan(0).And.LessThan(5));
        });
    }

    [Test]
    public void SilentNodeCountsAgainstASpotWhereItWasLoud()
    {
        // Both spots match a, b, c exactly; only the second was right next to node e, which the device
        // does not hear. Shared nodes alone cannot tell them apart; a missing-node rssi can.
        var spots = new[]
        {
            F(0, 0, ("a", -60), ("b", -70), ("c", -80)),
            F(9, 9, ("a", -60), ("b", -70), ("c", -80), ("e", -45)),
        };
        var reading = new Dictionary<string, double> { ["a"] = -60, ["b"] = -70, ["c"] = -80 };
        var withFloor = FingerprintLocator.Match(reading, spots.Reverse(), k: 1, minCommon: 3, missingRssi: -100)!;
        Assert.That((withFloor.Location.X, withFloor.Location.Y), Is.EqualTo((0.0, 0.0)));
    }

    [Test]
    public void ContinuousSurfaceLandsBetweenSpotsForAnInBetweenReading()
    {
        // A line of spots 1 m apart where node a fades and node b grows along x.
        var line = Enumerable.Range(0, 7).Select(i => F(i, 0, ("a", -50 - 5 * i), ("b", -80 + 5 * i), ("c", -70))).ToList();
        var surface = new FingerprintSurface(line, bandwidth: 0.6, missingRssi: -100);

        var onSpot = surface.Match(new Dictionary<string, double> { ["a"] = -60, ["b"] = -70, ["c"] = -70 }, minCommon: 3)!;
        Assert.That(onSpot.Location.X, Is.EqualTo(2).Within(0.3));

        // Halfway between x=2 and x=3, and 8 dB hotter overall: still lands between them.
        var between = surface.Match(new Dictionary<string, double> { ["a"] = -54.5, ["b"] = -59.5, ["c"] = -62 }, minCommon: 3)!;
        Assert.That(between.Location.X, Is.EqualTo(2.5).Within(0.3));
    }

    [Test]
    public void MapFileIsReadCaseInsensitivelyAndReloadedWhenChanged()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fp-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(path, """{"fingerprints":[{"floor":"First","x":1,"y":2,"z":0,"rssi":{"Node_A":-60}}]}""");
            var map = new FingerprintMap(path);
            Assert.That(map.ForFloor("first").Single().Rssi["node_a"], Is.EqualTo(-60));

            File.WriteAllText(path, """{"fingerprints":[]}""");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            Assert.That(map.All, Is.Empty);
        }
        finally { File.Delete(path); }
    }

    [TestFixture]
    public class RoomPrediction
    {
        // A single floor with a room layout the default config does not define, so the only rooms in
        // play are the ones this test adds. Two rooms 3 m tall with a 1 m gap between them at x = 5..6:
        // the gap belongs to no polygon, which is where the old "room of the averaged coordinate"
        // logic returned null.
        private const string FloorId = "room-test-floor";

        private static ConfigRoom Room(string name, double x0, double x1) => new()
        {
            Name = name,
            Points = new[] { new[] { x0, 0.0 }, new[] { x1, 0.0 }, new[] { x1, 3.0 }, new[] { x0, 3.0 } }
        };

        private readonly List<FingerprintSurvey> _surveys = new();

        [TearDown]
        public async Task Cleanup()
        {
            foreach (var survey in _surveys)
            {
                await survey.Loader.ConfigAsync();
                await survey.Loader.StopAsync(CancellationToken.None);
                survey.Loader.Dispose();
                Directory.Delete(survey.Dir, true);
            }
            _surveys.Clear();
        }

        private FingerprintSurvey Build(FingerprintConfig config)
        {
            var dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
            Directory.CreateDirectory(dir);
            var loader = new ConfigLoader(dir);
            var mqtt = new Mock<IMqttCoordinator>();
            var state = new State(loader, new NodeTelemetryStore(mqtt.Object));

            var floor = new Floor();
            floor.Update(loader.Config!, new ConfigFloor
            {
                Id = FloorId, Name = "Room Test",
                Bounds = new[] { new[] { 0.0, 0.0, 0.0 }, new[] { 9.0, 3.0, 3.0 } },
                Rooms = new[] { Room("Left", 0, 5), Room("Right", 6, 9) }
            });
            state.Floors[floor.Id] = floor;

            var nodes = new Dictionary<string, Node>();
            foreach (var id in new[] { "a", "b", "c" })
            {
                var node = new Node(id, NodeSourceType.Config);
                node.Update(loader.Config!, new ConfigNode { Name = id, Point = new[] { 1.0, 0.0, 2.0 } }, new[] { floor });
                state.Nodes[id] = node;
                nodes[id] = node;
            }
            var survey = new FingerprintSurvey(dir, loader, state, floor, nodes, config);
            _surveys.Add(survey);
            return survey;
        }

        private sealed record FingerprintSurvey(string Dir, ConfigLoader Loader, State State, Floor Floor,
            Dictionary<string, Node> Nodes, FingerprintConfig Config);

        private static Fingerprint Spot(double x, double y, params (string Node, double Rssi)[] r) =>
            new() { Floor = FloorId, X = x, Y = y, Z = 0, Rssi = r.ToDictionary(a => a.Node, a => a.Rssi, StringComparer.OrdinalIgnoreCase) };

        private static (Scenario Scenario, bool Located) Locate(FingerprintSurvey s, IReadOnlyList<Fingerprint> spots, params (string Node, double Rssi)[] reading)
        {
            var path = Path.Combine(s.Dir, s.Config.File);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
            {
                fingerprints = spots
            }));

            var device = new Device($"dev-{Guid.NewGuid()}", null, TimeSpan.FromSeconds(30));
            foreach (var (node, rssi) in reading)
                device.Nodes[node] = new DeviceToNode(device, s.Nodes[node]) { Rssi = rssi, LastHit = DateTime.UtcNow };

            var locator = new FingerprintLocator(device, s.Floor, new FingerprintMap(path), s.Config);
            var scenario = new Scenario(s.Loader.Config, locator, FloorId);
            var located = locator.Locate(scenario);
            return (scenario, located);
        }

        [Test]
        public void FixOutsideEveryRoomKeepsTheFloor()
        {
            var s = Build(new FingerprintConfig { MinCommon = 3, Mode = "knn", K = 1 });
            var spots = new[] { Spot(5.5, 1.5, ("a", -50), ("b", -70), ("c", -80)) };
            var (scenario, located) = Locate(s, spots, ("a", -50), ("b", -70), ("c", -80));

            Assert.Multiple(() =>
            {
                Assert.That(located, Is.True);
                Assert.That(scenario.Room, Is.Null);
                Assert.That(scenario.Floor, Is.SameAs(s.Floor));
            });
        }

        [Test]
        public void RoomComesFromMatchingSpotsEvenWhenTheFixLandsInAGapBetweenRooms()
        {
            // The two surveyed spots flank the gap: one inside the left room, one inside the right.
            // Their weighted average lands near x = 5.5, in no room. The match is a clear tie broken
            // by weight, so the room must come from the spots, not the coordinate.
            var s = Build(new FingerprintConfig { MinCommon = 3, Mode = "knn", K = 2 });
            var spots = new[]
            {
                Spot(4.5, 1.5, ("a", -50), ("b", -70), ("c", -80)),
                Spot(6.5, 1.5, ("a", -70), ("b", -50), ("c", -80))
            };
            // Symmetric patterns: an even reading averages to exactly x = 5.5, in no room.
            var (scenario, located) = Locate(s, spots, ("a", -60), ("b", -60), ("c", -80));

            Assert.That(located, Is.True);
            Assert.That(scenario.Location.X, Is.EqualTo(5.5).Within(0.01));
            Assert.That(scenario.Room?.Name, Is.AnyOf("Left", "Right"), "a fingerprint fix must still report a room");
        }

        [Test]
        public void RoomFollowsTheBetterMatchingSpot()
        {
            // The reading matches the spot in the right room far better, while the left spot is a
            // valid-but-worse match. The average of the two still lands in the gap, so only the
            // matched spots can say "Right".
            var s = Build(new FingerprintConfig { MinCommon = 3, Mode = "knn", K = 2 });
            var spots = new[]
            {
                Spot(4.5, 1.5, ("a", -50), ("b", -75), ("c", -80)),
                Spot(6.5, 1.5, ("a", -70), ("b", -50), ("c", -80))
            };
            // Near the midpoint of the two patterns: the right spot still matches better, but the
            // weight is balanced enough that the averaged coordinate stays in the gap.
            var (scenario, located) = Locate(s, spots, ("a", -62), ("b", -58), ("c", -80));

            Assert.That(located, Is.True);
            Assert.That(scenario.Location.X, Is.GreaterThan(5).And.LessThan(6), "the fix sits in the gap");
            Assert.That(scenario.Room?.Name, Is.EqualTo("Right"));
        }
    }
}
