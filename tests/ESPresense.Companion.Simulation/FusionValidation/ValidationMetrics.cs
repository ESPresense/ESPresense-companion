using System;
using System.Collections.Generic;
using System.Linq;

namespace ESPresense.Simulation.FusionValidation;

/// <summary>A labeled window where BLE-only room assignment is known to flap, e.g. the
/// `phone:darrell-15-pro` Laundry/Family case from 2026-11-03 cited in ESPA-463.</summary>
public sealed record FlapWindow(string DeviceId, DateTimeOffset Start, DateTimeOffset End);

/// <summary>How long after (positive) or before (negative, "CSI led HA") a ground-truth
/// occupancy transition the fused output reported the same room becoming occupied.
/// Null <see cref="Latency"/> means no matching fusion event was found in the search window.</summary>
public sealed record LatencySample(string Room, DateTimeOffset HaTransitionAt, DateTimeOffset? FusionTransitionAt, TimeSpan? Latency);

public sealed class ValidationReport
{
    public double FlapResolutionRate { get; init; }
    public int FlapSamples { get; init; }
    public double AttributionAccuracy { get; init; }
    public int AttributionSamples { get; init; }
    public double FalseUnknownRate { get; init; }
    public int UnknownEvents { get; init; }
    public List<LatencySample> Latencies { get; init; } = new();
}

/// <summary>
/// Scores a fused-output event log against ground truth derived from HA history.
/// Implements the four metrics ESPA-463 requires:
///   1. Flap resolution — does fused attribution hold the correct room during a
///      documented BLE flapping window?
///   2. Attribution accuracy — "who entered" vs device_tracker ground truth, overall.
///   3. False "unknown occupant" rate — Unknown=true events where the room was
///      actually empty per the occupancy binary sensor.
///   4. Latency — fused occupied-transition time vs HA's, including whether CSI leads.
/// </summary>
public static class ValidationMetrics
{
    public static ValidationReport Compute(
        IReadOnlyList<FusionRoomEvent> events,
        GroundTruth groundTruth,
        IReadOnlyList<FlapWindow> flapWindows,
        TimeSpan latencySearchWindow)
    {
        var (flapRate, flapSamples) = FlapResolutionRate(events, groundTruth, flapWindows);
        var (attribAcc, attribSamples) = AttributionAccuracy(events, groundTruth);
        var (falseUnknownRate, unknownEvents) = FalseUnknownRate(events, groundTruth);
        var latencies = Latency(events, groundTruth, latencySearchWindow);

        return new ValidationReport
        {
            FlapResolutionRate = flapRate,
            FlapSamples = flapSamples,
            AttributionAccuracy = attribAcc,
            AttributionSamples = attribSamples,
            FalseUnknownRate = falseUnknownRate,
            UnknownEvents = unknownEvents,
            Latencies = latencies
        };
    }

    /// <summary>At each fused event touching a flapping device within its labeled window,
    /// does the room the device is attributed to match the room HA's own device_tracker
    /// resolved for that same instant?</summary>
    public static (double Rate, int Samples) FlapResolutionRate(
        IReadOnlyList<FusionRoomEvent> events,
        GroundTruth groundTruth,
        IReadOnlyList<FlapWindow> flapWindows)
    {
        int correct = 0, total = 0;
        foreach (var window in flapWindows)
        {
            var relevant = events
                .Where(e => e.Timestamp >= window.Start && e.Timestamp <= window.End)
                .Where(e => e.Occupied && e.Who.Contains(window.DeviceId))
                .OrderBy(e => e.Timestamp);

            foreach (var e in relevant)
            {
                var truth = groundTruth.DeviceRoomAt(window.DeviceId, e.Timestamp);
                total++;
                if (truth is not null && string.Equals(truth, e.Room, StringComparison.OrdinalIgnoreCase))
                    correct++;
            }
        }
        return total == 0 ? (double.NaN, 0) : ((double)correct / total, total);
    }

    /// <summary>Across the full run, does each device named in a fused event's `who` list
    /// match the room device_tracker ground truth has it in at that timestamp?</summary>
    public static (double Rate, int Samples) AttributionAccuracy(
        IReadOnlyList<FusionRoomEvent> events,
        GroundTruth groundTruth)
    {
        int correct = 0, total = 0;
        foreach (var e in events.Where(e => e.Occupied && e.Who.Count > 0))
        {
            foreach (var deviceId in e.Who)
            {
                var truth = groundTruth.DeviceRoomAt(deviceId, e.Timestamp);
                total++;
                if (truth is not null && string.Equals(truth, e.Room, StringComparison.OrdinalIgnoreCase))
                    correct++;
            }
        }
        return total == 0 ? (double.NaN, 0) : ((double)correct / total, total);
    }

    /// <summary>Among events flagging an untracked occupant, how many land in a room the
    /// occupancy binary sensor says was empty — i.e. a false alarm rather than a real
    /// untagged person.</summary>
    public static (double Rate, int UnknownEvents) FalseUnknownRate(
        IReadOnlyList<FusionRoomEvent> events,
        GroundTruth groundTruth)
    {
        var unknownEvents = events.Where(e => e.Unknown).ToList();
        if (unknownEvents.Count == 0) return (double.NaN, 0);

        int falseAlarms = unknownEvents.Count(e => !groundTruth.IsRoomOccupied(e.Room, e.Timestamp));
        return ((double)falseAlarms / unknownEvents.Count, unknownEvents.Count);
    }

    /// <summary>For each ground-truth room-occupied-start transition, find the nearest
    /// fused occupied-start transition for the same room within <paramref name="searchWindow"/>
    /// and report the signed latency (positive = fusion observed it after HA; negative =
    /// fusion/CSI led HA, as documented for the Laundry 18:35 case in ESPA-463).</summary>
    public static List<LatencySample> Latency(
        IReadOnlyList<FusionRoomEvent> events,
        GroundTruth groundTruth,
        TimeSpan searchWindow)
    {
        // CsiFusionService.Tick emits a snapshot on any change, including Who/Unknown changing while a
        // room stays occupied -- so naively keeping every Occupied==true event (as this used to) counts
        // those as additional "starts" too. Keep only rising edges: an occupied event whose room's
        // previous event was not occupied. The synthetic fixture has one rising edge per room and so
        // never caught this; real data, with devices joining/leaving mid-episode, does.
        var fusionStarts = events
            .GroupBy(e => e.Room)
            .ToDictionary(g => g.Key, g =>
            {
                var starts = new List<DateTimeOffset>();
                var wasOccupied = false;
                foreach (var e in g.OrderBy(e => e.Timestamp))
                {
                    if (e.Occupied && !wasOccupied) starts.Add(e.Timestamp);
                    wasOccupied = e.Occupied;
                }
                return starts;
            });

        var samples = new List<LatencySample>();
        foreach (var interval in groundTruth.RoomOccupancy)
        {
            DateTimeOffset? nearest = null;
            if (fusionStarts.TryGetValue(interval.Room, out var times))
            {
                nearest = times
                    .Where(t => (t - interval.Start).Duration() <= searchWindow)
                    .OrderBy(t => (t - interval.Start).Duration())
                    .Cast<DateTimeOffset?>()
                    .FirstOrDefault();
            }

            samples.Add(new LatencySample(
                interval.Room,
                interval.Start,
                nearest,
                nearest is null ? null : nearest.Value - interval.Start));
        }
        return samples;
    }
}
