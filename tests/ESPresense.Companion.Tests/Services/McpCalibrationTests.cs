using System.Text.Json;
using ESPresense.Companion.Tests.Controllers;

namespace ESPresense.Companion.Tests.Services;

/// <summary>
/// MCP <c>get_calibration</c> and <c>state://calibration</c> return the same data as GET /api/state/calibration,
/// anchored devices included (#1836).
/// </summary>
public class McpCalibrationTests
{
    private CalibrationFixture _f = null!;

    [SetUp]
    public async Task Setup() => _f = await CalibrationFixture.CreateAsync();

    [TearDown]
    public async Task TearDown() => await _f.DisposeAsync();

    [Test]
    public async Task GetCalibrationTool_MatchesRestPayload()
    {
        var rest = JsonSerializer.Serialize(_f.CreateController().GetCalibration(), CalibrationFixture.RestJson);
        var mcp = await _f.CreateMcp().GetCalibrationTool();

        // Same settings as REST, so the whole payload is byte-identical, not just the matrix.
        Assert.That(mcp, Is.EqualTo(rest));
        Assert.That(mcp, Is.EqualTo(CalibrationWireFormatTests.ExpectedRestJson));
    }

    [Test]
    public async Task GetCalibrationTool_IncludesAnchoredDeviceRows()
    {
        var mcp = JsonDocument.Parse(await _f.CreateMcp().GetCalibrationTool()).RootElement;

        Assert.That(mcp.GetProperty("anchored").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(new[] { "Anchor One" }));
        var cell = mcp.GetProperty("matrix").GetProperty("Anchor One").GetProperty("Alpha");
        Assert.That(cell.GetProperty("mapDistance").GetDouble(), Is.EqualTo(2));
        Assert.That(cell.GetProperty("distance").GetDouble(), Is.EqualTo(2.25));
        Assert.That(cell.GetProperty("diff").GetDouble(), Is.EqualTo(0.25));
        Assert.That(cell.GetProperty("percent").GetDouble(), Is.EqualTo(0.125));
    }

    [Test]
    public async Task GetCalibrationTool_LeavesOutReceiversThatMove()
    {
        var mcp = JsonDocument.Parse(await _f.CreateMcp().GetCalibrationTool()).RootElement;

        var row = mcp.GetProperty("matrix").GetProperty("Anchor One");
        Assert.That(row.EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "Alpha", "Bravo" }),
            "Delta is on a car (stationary: false): no fixed distance to the anchor, so no cell and no R/RMSE pair");
    }

    [Test]
    public async Task CalibrationResource_MatchesTool()
    {
        var mcp = _f.CreateMcp();
        Assert.That(await mcp.GetCalibrationResource(), Is.EqualTo(await mcp.GetCalibrationTool()));
    }
}
