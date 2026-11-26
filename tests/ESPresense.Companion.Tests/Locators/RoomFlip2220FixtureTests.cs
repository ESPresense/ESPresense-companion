using System.Text.Json;
using System.Text.Json.Serialization;
using ESPresense.Models;
using ESPresense.Utils;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Companion.Tests.Locators;

/// <summary>
/// Characterization tests over the #2220 wrong-room field capture
/// (TestData/room-flip-2220, contributed by @plackettsj1 on 2026-11-25; see its README).
///
/// The capture carries both topic trees on one clock, so each record in <c>reports[]</c> is an
/// atomic pair: the state topic value and the attributes payload that
/// <see cref="DeviceReport.From"/> built from one snapshot of one device in one call. In that pair
/// the room comes from the winning scenario (<c>BestScenario.Room</c>) and the coordinates come
/// from the Kalman-filtered position (<c>Device.Location</c>) -- two different estimates, published
/// as if they agreed.
///
/// They do not, and that is what these tests pin. The room containing the published coordinates is
/// resolved here by the production path (<see cref="SpatialUtils.FindFloorAndRoom"/>) against the
/// contributor's own geometry, so a change in room containment moves these numbers too.
///
/// These are deliberately GREEN on the broken behaviour: they record it numerically rather than
/// leaving a red build behind. A build that fixes #2220 takes every
/// <c>StateEqualsSolvedRoom</c> to the full report count and will trip
/// <see cref="StateAgreesWithItsOwnCoordinates_Characterization"/>. That is the regression lock --
/// update the pinned numbers in the fixture's <c>expect</c> block when it fires, and say which
/// direction they moved.
/// </summary>
[TestFixture]
public class RoomFlip2220FixtureTests
{
    private const string FixtureFile = "room-flip-2220.json";

    private Fixture _fx = null!;
    private List<Floor> _floors = null!;

    /// <summary>Reports grouped by beacon, in capture order. Order is the pairing; see the README.</summary>
    private Dictionary<string, List<Report>> _byBeacon = null!;

    [OneTimeSetUp]
    public void LoadFixture()
    {
        var dir = Environment.GetEnvironmentVariable("ESPRESENSE_ROOM_FLIP_DIR");
        if (string.IsNullOrEmpty(dir))
            dir = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData", "room-flip-2220");
        var path = Path.Combine(dir, FixtureFile);
        if (!File.Exists(path))
            Assert.Ignore($"#2220 fixture not available: {path} not found");

        _fx = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var config = new Config();
        _floors = _fx.FloorBounds.Select(kv =>
        {
            var floor = new Floor();
            floor.Update(config, new ConfigFloor
            {
                Id = kv.Key,
                Name = _fx.FloorNames[kv.Key],
                Bounds = kv.Value,
                Rooms = _fx.Rooms.Where(r => r.Floor == kv.Key)
                    .Select(r => new ConfigRoom { Id = r.Id, Name = r.Id, Points = r.Points })
                    .ToArray()
            });
            return floor;
        }).ToList();

        _byBeacon = _fx.Reports.GroupBy(r => r.Beacon)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
    }

    /// <summary>The room the production path puts a report's own published coordinates in.</summary>
    private string? SolvedRoom(Report r) =>
        SpatialUtils.FindFloorAndRoom(new Point3D(r.X, r.Y, r.Z), _floors).room?.Name;

    private static IEnumerable<string> Beacons => new[] { "Green", "Yellow", "Blue", "Orange" };

    /// <summary>
    /// The defect, as a number: how many of a beacon's reports name the room its own published
    /// coordinates are actually in. Everything else in this file exists to stop this one being
    /// explained away.
    /// </summary>
    [TestCaseSource(nameof(Beacons))]
    public void StateAgreesWithItsOwnCoordinates_Characterization(string beacon)
    {
        var reports = _byBeacon[beacon];
        var expect = _fx.Expect.Consistency[beacon];

        var agree = reports.Count(r => r.State == SolvedRoom(r));

        Assert.Multiple(() =>
        {
            Assert.That(reports, Has.Count.EqualTo(expect.Reports), "report count");
            Assert.That(agree, Is.EqualTo(expect.StateEqualsSolvedRoom),
                $"{beacon}: reports whose state names the room containing their own x/y/z. "
                + "If this rose, #2220 got better and the fixture's expect block needs updating.");
        });
    }

    /// <summary>
    /// The coordinates are not the ambiguous half. Every report of a given beacon resolves to
    /// exactly one room across the whole capture, so the flipping cannot be blamed on the solver
    /// wandering across a boundary.
    /// </summary>
    [TestCaseSource(nameof(Beacons))]
    public void PublishedCoordinatesResolveToOneRoomForTheWholeCapture(string beacon)
    {
        var solved = _byBeacon[beacon]
            .GroupBy(SolvedRoom)
            .ToDictionary(g => g.Key ?? "<floor-only>", g => g.Count());

        Assert.That(solved, Is.EqualTo(_fx.Expect.SolvedRoom[beacon]),
            $"{beacon}: room containing the published coordinates, by report count");
        Assert.That(solved, Has.Count.EqualTo(1), $"{beacon}: coordinates never leave one room");
    }

    /// <summary>The label is the half that moves: the published room walks the vertical stack.</summary>
    [TestCaseSource(nameof(Beacons))]
    public void PublishedRoomFlipsRepeatedly(string beacon)
    {
        var reports = _byBeacon[beacon];
        var expect = _fx.Expect.Flips[beacon];

        var changes = reports.Zip(reports.Skip(1)).Count(p => p.First.State != p.Second.State);

        Assert.Multiple(() =>
        {
            Assert.That(changes, Is.EqualTo(expect.StateChanges), $"{beacon}: room changes");
            Assert.That(reports.Select(r => r.State).Distinct().OrderBy(s => s, StringComparer.Ordinal),
                Is.EqualTo(expect.DistinctStates), $"{beacon}: rooms visited");
            Assert.That(changes, Is.GreaterThan(0),
                $"{beacon}: a capture with no flip cannot stand in for #2220");
        });
    }

    /// <summary>
    /// The only beacon in this capture with a reporter-stated room: Green hung in the Sitting Room
    /// throughout. The coordinates agree with him for every report; the label does not. Outside
    /// confirmation that the solver is right and the labeller is wrong, rather than the fixture
    /// just disagreeing with itself.
    /// </summary>
    [Test]
    public void GreenCoordinatesMatchTheReporterStatedRoom()
    {
        var green = _fx.Beacons.Single(b => b.Label == "Green");
        Assert.That(green.TruthRoom, Is.EqualTo("Sitting Room"), "fixture truth room for Green");

        var reports = _byBeacon["Green"];
        Assert.Multiple(() =>
        {
            Assert.That(reports.Count(r => SolvedRoom(r) == green.TruthRoom), Is.EqualTo(reports.Count),
                "Green reports whose coordinates are in the reporter's stated room");
            Assert.That(reports.Count(r => r.State == green.TruthRoom),
                Is.EqualTo(_fx.Expect.Consistency["Green"].StateEqualsSolvedRoom),
                "Green reports whose published room is the reporter's stated room");
        });
    }

    /// <summary>
    /// Guards the fixture's own pairing. <c>state</c> and <c>best_scenario</c> come from the same
    /// scenario by construction, so a correctly ordered fixture has every report's room on its
    /// own best_scenario floor. Pairing by the 1-second timestamps instead of by array order drops
    /// this to 73-90%, which would quietly corrupt every number above.
    /// </summary>
    [TestCaseSource(nameof(Beacons))]
    public void ReportPairingIsIntactForEveryReport(string beacon)
    {
        var floorOfRoom = _fx.Rooms.ToDictionary(r => r.Id, r => _fx.FloorNames[r.Floor]);
        var reports = _byBeacon[beacon];
        var expect = _fx.Expect.Consistency[beacon];

        var onFloor = reports.Count(r =>
            floorOfRoom.TryGetValue(r.State, out var f) && f == r.BestScenario);

        Assert.Multiple(() =>
        {
            Assert.That(onFloor, Is.EqualTo(expect.StateOnBestScenarioFloor), "pinned pairing count");
            Assert.That(onFloor, Is.EqualTo(reports.Count),
                $"{beacon}: every report's room must sit on its own best_scenario floor");
        });
    }

    /// <summary>
    /// The stability gate, measured on the Companion's own published position rather than on the
    /// node readings feeding it. It cannot be satisfied by a build that destabilises its inputs,
    /// and it is what stops the assertions above passing vacuously: if the published position were
    /// wandering, "the label disagrees with the position" would say nothing about the labeller.
    /// </summary>
    [TestCaseSource(nameof(Beacons))]
    public void PublishedPositionIsStableAcrossTheCapture(string beacon)
    {
        var reports = _byBeacon[beacon];
        var expect = _fx.Expect.Windows[beacon];

        static double Sd(IEnumerable<double> xs)
        {
            var v = xs.ToList();
            var mean = v.Average();
            return Math.Round(Math.Sqrt(v.Sum(a => (a - mean) * (a - mean)) / v.Count), 3);
        }

        var sdX = Sd(reports.Select(r => r.X));
        var sdY = Sd(reports.Select(r => r.Y));
        var sdZ = Sd(reports.Select(r => r.Z));

        Assert.Multiple(() =>
        {
            Assert.That(sdX, Is.EqualTo(expect.SdX).Within(0.001), $"{beacon}: sd x");
            Assert.That(sdY, Is.EqualTo(expect.SdY).Within(0.001), $"{beacon}: sd y");
            Assert.That(sdZ, Is.EqualTo(expect.SdZ).Within(0.001), $"{beacon}: sd z");
            Assert.That(Math.Max(sdX, Math.Max(sdY, sdZ)), Is.LessThanOrEqualTo(0.25),
                $"{beacon}: published position must be stable for the flip to mean anything");
        });
    }

    /// <summary>
    /// Every node listing every floor is why the per-floor scenarios score alike and the winner
    /// oscillates. A fixture that loses this property stops reproducing #2220 -- it is the same
    /// property the contributor's own one-floor-per-node workaround removed when his flipping
    /// stopped.
    /// </summary>
    [Test]
    public void EveryNodeSpansEveryFloor()
    {
        var floorIds = _fx.FloorBounds.Keys.OrderBy(a => a, StringComparer.Ordinal).ToArray();
        Assert.That(_fx.Nodes, Is.Not.Empty);
        foreach (var node in _fx.Nodes)
            Assert.That(node.Floors.OrderBy(a => a, StringComparer.Ordinal), Is.EqualTo(floorIds),
                $"node {node.Id}");
    }

    /// <summary>
    /// Inputs are carried as rssi + the tag's advertised rssi@1m + rxAdj so a replay derives range
    /// with the Companion's own pipeline. The node's own distance is provenance only, so nothing
    /// above may read it -- but it has to be present and finite to be worth carrying.
    /// </summary>
    [Test]
    public void ReadingsCarryRawRssiInputs()
    {
        Assert.That(_fx.Readings, Is.Not.Empty);
        Assert.Multiple(() =>
        {
            Assert.That(_fx.Readings.All(r => r.Rssi < 0 && r.Rssi > -120), "rssi in range");
            Assert.That(_fx.Readings.All(r => r.RefRssi < 0 && r.RefRssi > -120), "rssi@1m in range");
            Assert.That(_fx.Readings.All(r => r.Dist > 0), "distance present for provenance");
            Assert.That(_fx.Readings.Select(r => r.Beacon).Distinct().OrderBy(a => a, StringComparer.Ordinal),
                Is.EqualTo(Beacons.OrderBy(a => a, StringComparer.Ordinal)));
        });
    }

    // ---- fixture shape ------------------------------------------------------------------------

    internal record Fixture(
        [property: JsonPropertyName("floorBounds")] Dictionary<string, double[][]> FloorBounds,
        [property: JsonPropertyName("floorNames")] Dictionary<string, string> FloorNames,
        [property: JsonPropertyName("rooms")] List<RoomSpec> Rooms,
        [property: JsonPropertyName("nodes")] List<NodeSpec> Nodes,
        [property: JsonPropertyName("beacons")] List<BeaconSpec> Beacons,
        [property: JsonPropertyName("reports")] List<Report> Reports,
        [property: JsonPropertyName("readings")] List<Reading> Readings,
        [property: JsonPropertyName("expect")] Expectations Expect);

    internal record RoomSpec(string Id, string Floor, double[][] Points);

    internal record NodeSpec(string Id, double X, double Y, double Z, List<string> Floors);

    internal record BeaconSpec(string Id, string Label, string? TruthRoom, string? TruthSource, string? Note);

    internal record Report(int T, string Beacon, string State, double X, double Y, double Z,
        int? Confidence, int? Fixes, string? BestScenario);

    internal record Reading(int T, string Beacon, string Node, double Rssi,
        [property: JsonPropertyName("refRssi")] double RefRssi, double RxAdj, double Dist);

    internal record Expectations(
        Dictionary<string, ConsistencyExpect> Consistency,
        Dictionary<string, FlipExpect> Flips,
        [property: JsonPropertyName("solvedRoom")] Dictionary<string, Dictionary<string, int>> SolvedRoom,
        Dictionary<string, WindowExpect> Windows);

    internal record ConsistencyExpect(int Reports, int StateEqualsSolvedRoom, int StateOnBestScenarioFloor);

    internal record FlipExpect(List<string> DistinctStates, int StateChanges);

    internal record WindowExpect(int TFirst, int TLast, double SdX, double SdY, double SdZ);
}
