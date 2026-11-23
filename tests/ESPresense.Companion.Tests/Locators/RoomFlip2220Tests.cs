namespace ESPresense.Companion.Tests.Locators;

/// <summary>
/// The #2220 regression lock: "wrong room despite stable XYZ".
///
/// A static beacon at a surveyed point is replayed through the real scenario pipeline from a
/// captured MQTT trace. The room the allocator reports must not change while its own distance
/// inputs are stable, and must be the surveyed room. Both halves matter: zero transitions alone
/// would pass a build that parks the tag in the wrong room and stays there.
///
/// Two arms, differing in exactly one thing - the `locators` block:
///   * AsCaptured   - per-floor Nadaraya-Watson on all three floors, which is what the reporter
///                    was running. Every node is listed on every floor, so each floor's scenario
///                    sees the same stations and they score alike; that is the flip mechanism.
///   * MultiFloor   - one 3D solve with the floor taken from the containing room, which is what
///                    shipped in companion v2.6.3. It passes on these two traces; the reporter's
///                    own 2026-11-21 field test says it does not fully resolve his system, so read
///                    this arm as a lock on covered behaviour, not as proof the bug is closed.
///
/// Fixture provenance, and what this data can and cannot prove: TestData/room-flip-2220/README.md.
/// Per-log qualification: ESPA-570.
/// </summary>
public class RoomFlip2220Tests
{
    /// <summary>Verbatim from the reporter's config (attachment 25887337).</summary>
    private const string AsCapturedLocators = """
        locators:
          nadaraya_watson:
            enabled: true
            floors: ["lower_ground_floor", "ground_floor", "first_floor"]
            bandwidth: 0.5
            kernel: gaussian
          nearest_node:
            enabled: true
            max_distance: 10
        """;

    /// <summary>The shipped fix: multi_floor on, the per-floor locator off. Everything else as captured.</summary>
    private const string MultiFloorLocators = """
        locators:
          multi_floor:
            enabled: true
          nearest_node:
            enabled: true
            max_distance: 10
        """;

    private static readonly string[] Fixtures =
        ["green-2026-05-27T11.replay.jsonl", "green-2026-05-26T14.replay.jsonl"];

    /// <summary>
    /// The gate, not an assertion. If the inputs are not stable across the window then "the room
    /// changed while the distances held still" is not what the trace shows, and the no-flip
    /// assertion below would pass or fail for the wrong reason.
    ///
    /// Every station with in-window readings is checked, because every one of them is fed to
    /// fusion. Two tiers, because the tight bound is only meaningful on a dense sample:
    ///   * dense (>= MinSamplesInWindow) - ESPA-570's measured bounds, rescaled into the domain
    ///     actually fed to the locators (see RoomFlipReplay.DistanceScale).
    ///   * sparse (2..MinSamplesInWindow-1) - twice those bounds. An sd estimated from a handful
    ///     of samples is itself noisy, so holding it to the dense bound would fail on sampling
    ///     error rather than on instability. 2x still sits far below the scale this gate exists
    ///     to catch: the four events ESPA-570 rejected move means by 1.7-10.3 m across the window.
    /// Stations with a single reading are reported but cannot be measured.
    /// </summary>
    [TestCaseSource(nameof(Fixtures))]
    public void InputsAreStableAcrossTheWindow(string file)
    {
        var truth = RoomFlipReplay.LoadTruth();
        var spec = truth.Event(file);
        var readings = RoomFlipReplay.LoadReadings(file, spec.Beacon);
        var scale = RoomFlipReplay.DistanceScale(truth.Replay);
        var stats = RoomFlipReplay.WindowStability(readings, spec, truth.Replay);

        Assert.That(stats.Values.Any(v => v.Dense), Is.True,
            $"no station reached {spec.MinSamplesInWindow} samples in the window - the fixture is unusable");

        foreach (var (node, s) in stats)
        {
            var tier = s.Dense ? 1.0 : 2.0;
            Assert.That(s.Sd, Is.LessThanOrEqualTo(tier * 0.75 * scale),
                $"{node}: distance sd {s.Sd:F2} m over {s.N} samples ({(s.Dense ? "dense" : "sparse")}) - input not stable");
            Assert.That(s.P5ToP95, Is.LessThanOrEqualTo(tier * 2.5 * scale),
                $"{node}: distance p5-p95 spread {s.P5ToP95:F2} m over {s.N} samples ({(s.Dense ? "dense" : "sparse")}) - input not stable");
        }

        foreach (var (node, s) in stats.OrderByDescending(kv => kv.Value.N))
            TestContext.WriteLine($"  {node,-16} n={s.N,3} {(s.Dense ? "dense " : "sparse")} " +
                                  $"sd={s.Sd:F3} p5-p95={s.P5ToP95:F3}");
        TestContext.WriteLine($"{file}: {stats.Count} measurable stations, " +
                              $"{stats.Values.Count(v => v.Dense)} dense; bounds " +
                              $"dense {0.75 * scale:F2}/{2.5 * scale:F2} m, " +
                              $"sparse {1.5 * scale:F2}/{5.0 * scale:F2} m");
    }

    /// <summary>
    /// The regression lock. With the shipped fix enabled the room must be constant across the
    /// window, must be the surveyed room, and must come from the `multi_floor` scenario.
    ///
    /// That last clause is load-bearing. `ScenarioFusion` picks the best *current* scenario from
    /// every enabled locator, and this arm also enables `nearest_node` because the reporter ran
    /// it. If `multi_floor` ever stopped producing a fix, a current `nearest_node` scenario could
    /// supply the right room and the test would pass without exercising `multi_floor` at all -
    /// green for the wrong reason, and blind to exactly the regression it exists to catch.
    /// Asserted on every in-window pass, which is what these two fixtures do today.
    /// </summary>
    [TestCaseSource(nameof(Fixtures))]
    public async Task MultiFloor_DoesNotFlipTheRoom(string file)
    {
        var truth = RoomFlipReplay.LoadTruth();
        var spec = truth.Event(file);
        var expected = truth.Beacons[spec.Beacon].ExpectedRoom;

        var steps = await RoomFlipReplay.RunAsync(MultiFloorLocators, file, spec, truth.Replay);
        var inWindow = steps.Where(s => s.T >= spec.WindowStart && s.T <= spec.WindowEnd).ToList();

        // A build that reports nothing must fail here, not pass vacuously.
        Assert.That(inWindow, Is.Not.Empty, "no fusion pass produced a result inside the window");
        var rooms = inWindow.Select(s => s.Room).ToList();
        Assert.That(rooms, Has.No.Null, "the allocator produced a fix with no room inside the window");

        // Scenario.Label for MultiFloorMultilateralizer; a per-floor locator would read
        // "<locator> (<floor>)" and NearestNode "Nearest Node".
        var offMultiFloor = inWindow.Where(s => s.Scenario != MultiFloorLabel).ToList();
        Assert.That(offMultiFloor, Is.Empty,
            $"{offMultiFloor.Count} of {inWindow.Count} in-window passes were won by another " +
            $"locator, so this is not testing multi_floor: {Transitions(inWindow)}");

        var distinct = rooms.Distinct().ToList();
        Assert.That(distinct, Has.Count.EqualTo(1),
            $"room flipped across the window: {Transitions(inWindow)}");
        Assert.That(distinct[0], Is.EqualTo(expected),
            $"room was stable but wrong: {distinct[0]} (expected {expected})");
    }

    /// <summary>`Scenario.Label` for <c>MultiFloorMultilateralizer</c>.</summary>
    private const string MultiFloorLabel = "MultiFloor";

    /// <summary>
    /// Characterisation, not a requirement: what the per-floor config the reporter was running
    /// does with the same trace. It is recorded rather than asserted because the corpus holds
    /// only raw node observations - the flip timestamps came from the reporter's Home Assistant
    /// history, which was never uploaded - so this data cannot prove a build reproduces the flip,
    /// only that a build must not flip. Asserting a flip here would be asserting on evidence the
    /// fixture does not contain.
    /// </summary>
    [TestCaseSource(nameof(Fixtures))]
    public async Task AsCaptured_RoomBehaviourIsRecorded(string file)
    {
        var truth = RoomFlipReplay.LoadTruth();
        var spec = truth.Event(file);

        var steps = await RoomFlipReplay.RunAsync(AsCapturedLocators, file, spec, truth.Replay);
        var inWindow = steps.Where(s => s.T >= spec.WindowStart && s.T <= spec.WindowEnd).ToList();
        Assert.That(inWindow, Is.Not.Empty, "no fusion pass produced a result inside the window");

        TestContext.WriteLine($"{file} as-captured ({AsCapturedLocators.Split('\n')[1].Trim()}): " +
                              $"{inWindow.Select(s => s.Room).Distinct().Count()} distinct room(s) " +
                              $"over {inWindow.Count} passes -> {Transitions(inWindow)}");
    }

    private static string Transitions(IEnumerable<RoomFlipReplay.Step> steps)
    {
        var runs = new List<string>();
        foreach (var s in steps)
        {
            var label = $"{s.Room ?? "<none>"} [{s.Scenario ?? "-"}]";
            if (runs.Count == 0 || runs[^1] != label) runs.Add(label);
        }

        return string.Join(" -> ", runs);
    }
}
