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
///   * MultiFloor   - one 3D solve with the floor taken from the containing room, which is the
///                    fix that shipped in companion v2.6.3.
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
    /// assertion below would pass or fail for the wrong reason. Thresholds are ESPA-570's,
    /// measured on the logged distance field, rescaled into the domain actually fed to the
    /// locators (see RoomFlipReplay.DistanceScale).
    /// </summary>
    [TestCaseSource(nameof(Fixtures))]
    public void InputsAreStableAcrossTheWindow(string file)
    {
        var truth = RoomFlipReplay.LoadTruth();
        var spec = truth.Event(file);
        var readings = RoomFlipReplay.LoadReadings(file, spec.Beacon);
        var scale = RoomFlipReplay.DistanceScale(truth.Replay);
        var stats = RoomFlipReplay.WindowStability(readings, spec, truth.Replay);

        Assert.That(stats, Is.Not.Empty,
            $"no station reached {spec.MinSamplesInWindow} samples in the window - the fixture is unusable");

        foreach (var (node, s) in stats)
        {
            Assert.That(s.Sd, Is.LessThanOrEqualTo(0.75 * scale),
                $"{node}: distance sd {s.Sd:F2} m over {s.N} samples - input not stable");
            Assert.That(s.P5ToP95, Is.LessThanOrEqualTo(2.5 * scale),
                $"{node}: distance p5-p95 spread {s.P5ToP95:F2} m over {s.N} samples - input not stable");
        }

        TestContext.WriteLine($"{file}: {stats.Count} stations cleared {spec.MinSamplesInWindow} samples; " +
                              $"max sd {stats.Values.Max(v => v.Sd):F2} m, " +
                              $"max p5-p95 {stats.Values.Max(v => v.P5ToP95):F2} m " +
                              $"(bounds {0.75 * scale:F2} / {2.5 * scale:F2} m)");
    }

    /// <summary>
    /// The regression lock. With the shipped fix enabled the room must be constant across the
    /// window, and must be the surveyed room.
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

        var distinct = rooms.Distinct().ToList();
        Assert.That(distinct, Has.Count.EqualTo(1),
            $"room flipped across the window: {Transitions(inWindow)}");
        Assert.That(distinct[0], Is.EqualTo(expected),
            $"room was stable but wrong: {distinct[0]} (expected {expected})");
    }

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
