using System;
using System.Collections.Generic;
using ESPresense.Simulation.FusionValidation;

namespace ESPresense.Companion.Tests.FusionValidation;

/// <summary>
/// Exercises ValidationMetrics against synthetic fixtures built from the two reference
/// scenarios named in ESPA-463: `phone:darrell-15-pro` flapping Laundry/Family on
/// 2026-11-03, and the Laundry 18:35 case where CSI led HA's own occupancy sensor.
/// These are hand-built, not pulled from live HA (parts 1-4 of the fusion work were
/// still `todo` and HA access was unavailable when this harness was written) — they
/// exist to prove the metric math is correct so it's ready the moment real data lands.
/// </summary>
[TestFixture]
public class ValidationMetricsTests
{
    private static DateTimeOffset T(string hhmmss) => DateTimeOffset.Parse($"2026-11-03T{hhmmss}Z");

    [Test]
    public void FlapResolutionRate_scores_how_often_fusion_holds_the_ground_truth_room()
    {
        const string device = "darrell-15-pro";
        var window = new FlapWindow(device, T("18:30:00"), T("18:36:00"));

        // HA's own device_tracker (ground truth) holds "laundry" steady through the window.
        var groundTruth = new GroundTruth();
        groundTruth.DeviceRooms.Add(new DeviceRoomInterval(device, "laundry", T("18:00:00"), null));

        // Fused output mostly agrees but drops one blip into "family" (3 of 4 correct).
        var events = new List<FusionRoomEvent>
        {
            new(T("18:30:05"), "laundry", true, new[] { device }, false),
            new(T("18:30:35"), "family", true, new[] { device }, false),
            new(T("18:31:00"), "laundry", true, new[] { device }, false),
            new(T("18:31:30"), "laundry", true, new[] { device }, false),
        };

        var (rate, samples) = ValidationMetrics.FlapResolutionRate(events, groundTruth, new[] { window });

        Assert.That(samples, Is.EqualTo(4));
        Assert.That(rate, Is.EqualTo(0.75).Within(1e-9));
    }

    [Test]
    public void FlapResolutionRate_is_NaN_when_no_fusion_events_fall_in_the_window()
    {
        var window = new FlapWindow("darrell-15-pro", T("18:30:00"), T("18:36:00"));
        var (rate, samples) = ValidationMetrics.FlapResolutionRate(
            new List<FusionRoomEvent>(), new GroundTruth(), new[] { window });

        Assert.That(samples, Is.EqualTo(0));
        Assert.That(double.IsNaN(rate), Is.True);
    }

    [Test]
    public void AttributionAccuracy_checks_who_against_device_tracker_ground_truth()
    {
        var groundTruth = new GroundTruth();
        groundTruth.DeviceRooms.Add(new DeviceRoomInterval("alice", "office", T("21:00:00"), T("21:30:00")));
        groundTruth.DeviceRooms.Add(new DeviceRoomInterval("bob", "laundry", T("21:00:00"), T("21:30:00")));

        var events = new List<FusionRoomEvent>
        {
            new(T("21:10:00"), "office", true, new[] { "alice" }, false),   // correct
            new(T("21:10:00"), "office", true, new[] { "bob" }, false),    // wrong room for bob
        };

        var (accuracy, samples) = ValidationMetrics.AttributionAccuracy(events, groundTruth);

        Assert.That(samples, Is.EqualTo(2));
        Assert.That(accuracy, Is.EqualTo(0.5).Within(1e-9));
    }

    [Test]
    public void FalseUnknownRate_counts_unknown_events_where_the_room_was_actually_empty()
    {
        var groundTruth = new GroundTruth();
        groundTruth.RoomOccupancy.Add(new RoomOccupancyInterval("office", T("21:16:00"), T("21:18:00")));

        var events = new List<FusionRoomEvent>
        {
            // Mirrors the documented Office false alarm (1143-1230) at 21:16 with the
            // occupancy sensor off outside this window.
            new(T("21:25:00"), "office", true, Array.Empty<string>(), true),  // sensor off here -> false alarm
            new(T("21:17:00"), "office", true, Array.Empty<string>(), true), // sensor on here -> real
        };

        var (rate, unknownEvents) = ValidationMetrics.FalseUnknownRate(events, groundTruth);

        Assert.That(unknownEvents, Is.EqualTo(2));
        Assert.That(rate, Is.EqualTo(0.5).Within(1e-9));
    }

    [Test]
    public void Latency_reports_a_negative_value_when_fusion_CSI_leads_HA_occupancy()
    {
        // Laundry 18:35 case: CSI caught motion about a minute before HA's own occupancy
        // sensor fired.
        var groundTruth = new GroundTruth();
        groundTruth.RoomOccupancy.Add(new RoomOccupancyInterval("laundry", T("18:35:30"), null));

        var events = new List<FusionRoomEvent>
        {
            new(T("18:34:25"), "laundry", true, Array.Empty<string>(), false)
        };

        var samples = ValidationMetrics.Latency(events, groundTruth, TimeSpan.FromMinutes(10));

        Assert.That(samples, Has.Count.EqualTo(1));
        Assert.That(samples[0].Latency, Is.Not.Null);
        Assert.That(samples[0].Latency!.Value.TotalSeconds, Is.EqualTo(-65).Within(0.01));
    }

    [Test]
    public void Latency_matches_the_rising_edge_not_a_mid_occupancy_Who_change()
    {
        // CsiFusionService.Tick emits a snapshot on any change, including Who/Unknown changing
        // while a room stays occupied. A naive "keep every Occupied==true event" implementation
        // picks whichever of those lands closest to the HA transition instead of the real start --
        // the synthetic single-rising-edge fixtures never exercised this, real data does.
        var groundTruth = new GroundTruth();
        groundTruth.RoomOccupancy.Add(new RoomOccupancyInterval("office", T("21:17:00"), null));

        var events = new List<FusionRoomEvent>
        {
            new(T("21:16:30"), "office", true, Array.Empty<string>(), true),       // real rising edge
            new(T("21:16:59"), "office", true, new[] { "alice" }, false),           // same episode: Who changed
        };

        var samples = ValidationMetrics.Latency(events, groundTruth, TimeSpan.FromMinutes(10));

        Assert.That(samples[0].FusionTransitionAt, Is.EqualTo(T("21:16:30")),
            "must match the rising edge (21:16:30), not the later mid-episode event that only changed Who");
    }

    [Test]
    public void Latency_leaves_no_match_when_nothing_falls_within_the_search_window()
    {
        var groundTruth = new GroundTruth();
        groundTruth.RoomOccupancy.Add(new RoomOccupancyInterval("laundry", T("18:35:30"), null));

        var events = new List<FusionRoomEvent>
        {
            new(T("17:00:00"), "laundry", true, Array.Empty<string>(), false)
        };

        var samples = ValidationMetrics.Latency(events, groundTruth, TimeSpan.FromMinutes(10));

        Assert.That(samples[0].FusionTransitionAt, Is.Null);
        Assert.That(samples[0].Latency, Is.Null);
    }
}
