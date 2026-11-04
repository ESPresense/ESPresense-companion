using System.IO;
using ESPresense.Simulation.FusionValidation;

namespace ESPresense.Companion.Tests.FusionValidation;

[TestFixture]
public class HomeAssistantHistoryTests
{
    [Test]
    public void LoadFromFile_CarriesEntityIdForward_AcrossMinimalResponseRows()
    {
        // Home Assistant's minimal_response puts entity_id only on a series' first row; every
        // other row carries just state/last_changed. Without carrying the id forward, Parse used
        // to skip every row but the first per entity -- a live pull would lose almost every
        // transition while a round-tripped (SaveToFile'd) file hid the bug by writing entity_id
        // on every row.
        var json = """
        [
          [
            { "entity_id": "binary_sensor.office_occupancy", "state": "off", "last_changed": "2026-11-04T08:00:00Z" },
            { "state": "on", "last_changed": "2026-11-04T08:05:00Z" },
            { "state": "off", "last_changed": "2026-11-04T08:10:00Z" }
          ]
        ]
        """;

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, json);
            var history = HaHistoryClient.LoadFromFile(path);

            Assert.That(history.ContainsKey("binary_sensor.office_occupancy"), Is.True);
            Assert.That(history["binary_sensor.office_occupancy"], Has.Count.EqualTo(3),
                "all three rows must attribute to the series' entity, not just the first");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void LoadFromFile_SkipsRows_BeforeTheFirstEntityIdInASeries()
    {
        var json = """
        [
          [
            { "state": "off", "last_changed": "2026-11-04T08:00:00Z" },
            { "entity_id": "binary_sensor.office_occupancy", "state": "on", "last_changed": "2026-11-04T08:05:00Z" }
          ]
        ]
        """;

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, json);
            var history = HaHistoryClient.LoadFromFile(path);

            Assert.That(history["binary_sensor.office_occupancy"], Has.Count.EqualTo(1),
                "a row with no entity_id yet known for its series must still be skipped");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
