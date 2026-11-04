using System.Text.Json;
using ESPresense.Companion.Tests.Controllers;

namespace ESPresense.Companion.Tests.Services;

/// <summary>MCP tools serialize with the REST settings, so each returns the same JSON as its /api endpoint.</summary>
public class McpRestParityTests
{
    private CalibrationFixture _f = null!;

    [SetUp]
    public async Task Setup() => _f = await CalibrationFixture.CreateAsync();

    [TearDown]
    public async Task TearDown() => await _f.DisposeAsync();

    [Test]
    public async Task GetDevicesTool_MatchesRestShowAll()
    {
        var mcp = await _f.CreateMcp().GetDevicesTool();
        var rest = JsonSerializer.Serialize(_f.CreateController().GetDevices(showAll: true).ToList(), CalibrationFixture.RestJson);
        Assert.That(mcp, Is.EqualTo(rest));
        Assert.That(mcp, Does.Contain("\"id\":\"anchor-1\""), "camelCase property names, as REST");
    }

    [Test]
    public async Task GetConfigTool_MatchesRest()
    {
        var mcp = await _f.CreateMcp().GetConfigTool();
        var rest = JsonSerializer.Serialize(_f.CreateController().GetConfig(), CalibrationFixture.RestJson);
        Assert.That(mcp, Is.EqualTo(rest));
    }
}
