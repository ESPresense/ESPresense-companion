using System;
using System.IO;
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
        var json = """
        [
          [
            { "entity_id": "sensor.office_espresense_office_csi_variance", "state": "10", "last_changed": "2026-11-04T08:00:00Z" },
            { "entity_id": "sensor.office_espresense_office_csi_variance", "state": "10", "last_changed": "2026-11-04T08:00:05Z" },
            { "entity_id": "sensor.office_espresense_office_csi_variance", "state": "90", "last_changed": "2026-11-04T08:00:10Z" },
            { "entity_id": "sensor.office_espresense_office_csi_variance", "state": "90", "last_changed": "2026-11-04T08:00:15Z" }
          ],
          [
            { "entity_id": "binary_sensor.office_occupancy", "state": "off", "last_changed": "2026-11-04T07:59:00Z" },
            { "entity_id": "binary_sensor.office_occupancy", "state": "on", "last_changed": "2026-11-04T08:00:08Z" }
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
                "--end", "2026-11-04T08:00:30Z",
                "--output", outputPath,
                "--markdown", markdownPath
            });

            Assert.That(exit, Is.EqualTo(0));
            var reportJson = File.ReadAllText(outputPath);
            Assert.That(reportJson, Does.Contain("false_unknown_rate"));
            Assert.That(File.ReadAllText(markdownPath), Does.Contain("Window:"));
        }
        finally
        {
            File.Delete(exportPath);
            File.Delete(outputPath);
            File.Delete(markdownPath);
        }
    }
}
