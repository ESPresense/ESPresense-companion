using ESPresense.Controllers;
using ESPresense.Models;

namespace ESPresense.Companion.Tests.Controllers;

public class HistoryControllerTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);

    private static DeviceHistory Row(int seconds, bool best = true, string scenario = "MultiFloor", double x = 0) =>
        new() { Id = "dev-1", When = T0.AddSeconds(seconds), Best = best, Scenario = scenario, X = x };

    [Test]
    public void Thin_KeepsTheLastBestRowOfEachBucket_WhicheverScenarioWasBest()
    {
        var rows = new List<DeviceHistory>
        {
            Row(0, x: 1), Row(1, x: 2), Row(4, x: 3, scenario: "NearestNode"),
            Row(5, x: 4), Row(9, x: 5),
            Row(20, x: 6)
        };

        var thinned = HistoryController.Thin(rows, 5);

        Assert.That(thinned.Select(r => r.X), Is.EqualTo(new double?[] { 3, 5, 6 }), "one row per 5 s bucket, the last of each, in order");
    }

    [Test]
    public void Thin_KeepsEachNonBestScenarioAsItsOwnSeries()
    {
        var rows = new List<DeviceHistory>
        {
            Row(0, best: false, scenario: "A", x: 1), Row(1, best: false, scenario: "B", x: 2),
            Row(2, best: false, scenario: "A", x: 3), Row(2, best: true, scenario: "A", x: 4)
        };

        var thinned = HistoryController.Thin(rows, 60);

        Assert.That(thinned.Select(r => (r.Scenario, r.Best, r.X)), Is.EqualTo(new[] { ("A", false, (double?)3), ("B", false, (double?)2), ("A", true, (double?)4) }));
    }

    [Test]
    public void Thin_IsANoOpWithoutABucketSize()
    {
        var rows = new List<DeviceHistory> { Row(0), Row(1) };

        Assert.That(HistoryController.Thin(rows, 0), Is.SameAs(rows));
        Assert.That(HistoryController.Thin(new List<DeviceHistory>(), 5), Is.Empty);
    }
}
