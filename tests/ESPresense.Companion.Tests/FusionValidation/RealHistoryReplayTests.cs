using System;
using System.IO;
using System.Text.Json;
using ESPresense.Simulation.FusionValidation;

namespace ESPresense.Companion.Tests.FusionValidation;

/// <summary>
/// A smoke test for the ESPA-466 real-HA-history replay tool, using a tiny synthetic export in the
/// exact <see cref="HaHistoryClient.LoadFromFile"/> shape -- never the real, private export (that file
/// is local-only, per the PR description, and is never read by the test suite).
/// </summary>
[TestFixture]
public class RealHistoryReplayTests
{
    [Test]
    public void Run_OnASyntheticExport_ProducesAValidReportWithoutCrashing()
    {
        // 15 quiet readings (>= CsiMotionTracker.MinBaselineSamples) so the baseline is a real
        // percentile, not a tiny-sample average that an elevated reading itself drags upward, then two
        // consecutive elevated readings for sustained motion.
        var csiRows = new System.Text.StringBuilder();
        for (var i = 0; i < 15; i++)
            csiRows.Append($$"""{ "entity_id": "sensor.office_espresense_office_csi_variance", "state": "10", "last_changed": "2026-11-04T08:{{(i * 5 / 60):D2}}:{{(i * 5 % 60):D2}}Z" },""");
        csiRows.Append("""{ "entity_id": "sensor.office_espresense_office_csi_variance", "state": "90", "last_changed": "2026-11-04T08:01:15Z" },""");
        csiRows.Append("""{ "entity_id": "sensor.office_espresense_office_csi_variance", "state": "90", "last_changed": "2026-11-04T08:01:20Z" }""");

        var json = $$"""
        [
          [ {{csiRows}} ],
          [
            { "entity_id": "binary_sensor.office_occupancy", "state": "off", "last_changed": "2026-11-04T07:59:00Z" },
            { "entity_id": "binary_sensor.office_occupancy", "state": "on", "last_changed": "2026-11-04T08:01:18Z" }
          ],
          [
            { "entity_id": "device_tracker.phone_darrell_15_pro", "state": "office", "last_changed": "2026-11-04T07:59:30Z" }
          ]
        ]
        """;

        var exportPath = Path.GetTempFileName();
        var outputPath = Path.GetTempFileName();
        var markdownPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(exportPath, json);

            var exit = RealHistoryReplay.Run(new[]
            {
                "--ha-export", exportPath,
                "--start", "2026-11-04T08:00:00Z",
                "--end", "2026-11-04T08:02:00Z",
                "--output", outputPath,
                "--markdown", markdownPath
            });

            Assert.That(exit, Is.EqualTo(0));
            var reportJson = File.ReadAllText(outputPath);
            Assert.That(reportJson, Does.Contain("false_unknown_rate"));
            Assert.That(File.ReadAllText(markdownPath), Does.Contain("Window:"));

            // Pins the window-seeding fix: the device_tracker row says "office" from before Start and
            // never changes, so without seeding the last pre-window state, device.BestScenario stays
            // null for the whole replay and the office motion at 08:00:10 is wrongly scored as an
            // unknown occupant instead of being attributed to the device already there.
            using var report = JsonDocument.Parse(reportJson);
            Assert.That(report.RootElement.GetProperty("attribution_samples").GetInt32(), Is.GreaterThan(0),
                "the device already in the room before Start must be attributed when motion starts, not scored as unknown");
            Assert.That(report.RootElement.GetProperty("attribution_accuracy").GetDouble(), Is.EqualTo(1.0));
        }
        finally
        {
            File.Delete(exportPath);
            File.Delete(outputPath);
            File.Delete(markdownPath);
        }
    }
}
