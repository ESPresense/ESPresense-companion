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
        var rest = JsonDocument.Parse(JsonSerializer.Serialize(_f.CreateController().GetCalibration(), CalibrationFixture.RestJson)).RootElement;
        var mcpJson = await _f.CreateMcp().GetCalibrationTool();
        var mcp = JsonDocument.Parse(mcpJson).RootElement;

        // MCP keeps its own (PascalCase) property names; the matrix is keyed by names and cell keys, so it is byte-identical.
        Assert.That(mcp.GetProperty("Matrix").GetRawText(), Is.EqualTo(rest.GetProperty("matrix").GetRawText()));
        Assert.That(mcp.GetProperty("Anchored").GetRawText(), Is.EqualTo(rest.GetProperty("anchored").GetRawText()));
        foreach (var (mcpName, restName) in new[] { ("RMSE", "rmse"), ("R", "r"), ("BestRMSE", "bestRMSE"), ("BestR", "bestR") })
            Assert.That(mcp.GetProperty(mcpName).GetDouble(), Is.EqualTo(rest.GetProperty(restName).GetDouble()), mcpName);
    }

    [Test]
    public async Task GetCalibrationTool_IncludesAnchoredDeviceRows()
    {
        var mcp = JsonDocument.Parse(await _f.CreateMcp().GetCalibrationTool()).RootElement;

        Assert.That(mcp.GetProperty("Anchored").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(new[] { "Anchor One" }));
        var cell = mcp.GetProperty("Matrix").GetProperty("Anchor One").GetProperty("Alpha");
        Assert.That(cell.GetProperty("mapDistance").GetDouble(), Is.EqualTo(2));
        Assert.That(cell.GetProperty("distance").GetDouble(), Is.EqualTo(2.25));
        Assert.That(cell.GetProperty("diff").GetDouble(), Is.EqualTo(0.25));
        Assert.That(cell.GetProperty("percent").GetDouble(), Is.EqualTo(0.125));
    }

    [Test]
    public async Task CalibrationResource_MatchesTool()
    {
        var mcp = _f.CreateMcp();
        Assert.That(await mcp.GetCalibrationResource(), Is.EqualTo(await mcp.GetCalibrationTool()));
    }
}
