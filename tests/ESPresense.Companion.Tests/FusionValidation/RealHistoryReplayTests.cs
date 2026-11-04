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
        // 15 quiet readings (>= CsiMotionTracker.MinBaselineSamples), all BEFORE --start, so the
        // baseline is a real percentile built from genuine history rather than a tiny-sample average
        // that the elevated readings themselves would drag upward (CodeRabbit's counter-example: a lone
        // pre-window 10 plus in-window 90, 90 averages to a baseline the ratio never crosses). Then two
        // consecutive in-window elevated readings for sustained motion.
        var csiRows = new System.Text.StringBuilder();
        for (var i = 0; i < 15; i++)
        {
            var second = i * 5;
            csiRows.Append($$"""{ "entity_id": "sensor.office_espresense_office_csi_variance", "state": "10", "last_changed": "2026-11-04T07:{{(58 + second / 60):D2}}:{{(second % 60):D2}}Z" },""");
        }
        csiRows.Append("""{ "entity_id": "sensor.office_espresense_office_csi_variance", "state": "90", "last_changed": "2026-11-04T08:00:10Z" },""");
        csiRows.Append("""{ "entity_id": "sensor.office_espresense_office_csi_variance", "state": "90", "last_changed": "2026-11-04T08:00:15Z" }""");

        var json = $$"""
        [
          [ {{csiRows}} ],
          [
            { "entity_id": "binary_sensor.office_occupancy", "state": "on", "last_changed": "2026-11-04T07:00:00Z" }
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

            using var report = JsonDocument.Parse(reportJson);

            // Pins the window-seeding fix: the device_tracker row says "office" from before Start and
            // never changes, so without seeding the last pre-window state, device.BestScenario stays
            // null for the whole replay and the office motion is wrongly scored as an unknown occupant
            // instead of being attributed to the device already there.
            Assert.That(report.RootElement.GetProperty("attribution_samples").GetInt32(), Is.GreaterThan(0),
                "the device already in the room before Start must be attributed when motion starts, not scored as unknown");
            Assert.That(report.RootElement.GetProperty("attribution_accuracy").GetDouble(), Is.EqualTo(1.0));

            // Pins the full-pre-window-CSI-history fix: without it, the baseline is built from only the
            // two in-window elevated readings (seeded from a single pre-window value) and never reaches
            // MotionRatio, so no motion -- and thus no attribution sample -- would ever fire at all.
            // (Covered by the assertion above succeeding at all, since there'd be zero samples otherwise;
            // asserted again explicitly here so a regression reads as "motion never detected", not just
            // "accuracy wrong".)
            Assert.That(report.RootElement.GetProperty("unknown_events").GetInt32(), Is.EqualTo(0),
                "a sustained in-window motion with the device already attributed must not also count as an unknown occupant");

            // Pins the window-start-continuation exclusion: binary_sensor.office_occupancy was already
            // "on" long before --start and never changes, so WindowWithSeed clamps that to exactly
            // --start. That clamp is a windowing artifact, not a real transition -- if Latency scored it,
            // this room's only ground-truth sample would wrongly match the in-window motion event.
            var latency = report.RootElement.GetProperty("latency");
            Assert.That(latency.GetArrayLength(), Is.EqualTo(0),
                "a window-start continuation (not a real transition) must not produce a latency sample");
        }
        finally
        {
            File.Delete(exportPath);
            File.Delete(outputPath);
            File.Delete(markdownPath);
        }
    }
}
