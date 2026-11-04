using System;
using System.Collections.Generic;
using ESPresense.Simulation.FusionValidation;

namespace ESPresense.Companion.Tests.FusionValidation;

[TestFixture]
public class GroundTruthBuilderTests
{
    private static DateTimeOffset T(string hhmm) => DateTimeOffset.Parse($"2026-11-03T{hhmm}:00Z");

    [Test]
    public void Build_excludes_entities_in_the_exclusion_set()
    {
        // Family's occupancy sensor has been stuck "on" since 2026-11-03 09:03 per ESPA-463;
        // the harness must be able to drop it from ground truth without it polluting metrics.
        var history = new Dictionary<string, List<HaStateChange>>
        {
            ["binary_sensor.family_occupancy"] = new()
            {
                new("binary_sensor.family_occupancy", "on", T("09:03"))
            },
            ["binary_sensor.laundry_occupancy"] = new()
            {
                new("binary_sensor.laundry_occupancy", "on", T("15:41")),
                new("binary_sensor.laundry_occupancy", "off", T("15:43"))
            }
        };

        var gt = GroundTruthBuilder.Build(
            history,
            new HashSet<string> { "binary_sensor.family_occupancy" },
            e => e.Replace("binary_sensor.", "").Replace("_occupancy", ""),
            s => s);

        Assert.That(gt.RoomOccupancy, Has.Count.EqualTo(1));
        Assert.That(gt.RoomOccupancy[0].Room, Is.EqualTo("laundry"));
        Assert.That(gt.IsRoomOccupied("family", T("12:00")), Is.False);
    }

    [Test]
    public void Build_pairs_on_off_into_occupancy_intervals_and_leaves_the_last_one_open()
    {
        var history = new Dictionary<string, List<HaStateChange>>
        {
            ["binary_sensor.office_occupancy"] = new()
            {
                new("binary_sensor.office_occupancy", "off", T("21:00")),
                new("binary_sensor.office_occupancy", "on", T("21:25")),
                new("binary_sensor.office_occupancy", "off", T("21:40")),
                new("binary_sensor.office_occupancy", "on", T("22:00"))
            }
        };

        var gt = GroundTruthBuilder.Build(
            history, new HashSet<string>(),
            e => "office", s => s);

        Assert.That(gt.RoomOccupancy, Has.Count.EqualTo(2));
        Assert.That(gt.IsRoomOccupied("office", T("21:30")), Is.True);
        Assert.That(gt.IsRoomOccupied("office", T("21:10")), Is.False);
        Assert.That(gt.IsRoomOccupied("office", T("23:00")), Is.True, "last 'on' with no following 'off' should stay open");
    }

    [Test]
    public void Build_tracks_device_room_changes_and_treats_not_home_as_absent()
    {
        var history = new Dictionary<string, List<HaStateChange>>
        {
            ["device_tracker.darrell_15_pro"] = new()
            {
                new("device_tracker.darrell_15_pro", "laundry", T("18:00")),
                new("device_tracker.darrell_15_pro", "family", T("18:10")),
                new("device_tracker.darrell_15_pro", "not_home", T("18:20"))
            }
        };

        var gt = GroundTruthBuilder.Build(history, new HashSet<string>(), e => e, s => s);

        Assert.That(gt.DeviceRoomAt("darrell_15_pro", T("18:05")), Is.EqualTo("laundry"));
        Assert.That(gt.DeviceRoomAt("darrell_15_pro", T("18:15")), Is.EqualTo("family"));
        Assert.That(gt.DeviceRoomAt("darrell_15_pro", T("18:25")), Is.Null);
    }
}
