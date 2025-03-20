using ESPresense.Models;
using ESPresense.Services;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace ESPresense.Controllers;

[Route("api/node")]
[ApiController]
public class NodeController(NodeSettingsStore nodeSettingsStore, State state) : ControllerBase
{
    [HttpGet("{id}")]
    public NodeSettingsDetails Get(string id)
    {
        var nodeSettings = nodeSettingsStore.Get(id);
        var details = new List<KeyValuePair<string, string>>();
        if (nodeSettings?.Id != null && state.Nodes.TryGetValue(id, out var node))
            details.AddRange(node.GetDetails());
        return new NodeSettingsDetails(nodeSettings ?? new NodeSettings(id), details);
    }

    [HttpPut("{id}")]
    public Task Set(string id, [FromBody] NodeSettings ds)
    {
        Log.Information("Set {id} {@ds}", id, ds);
        return nodeSettingsStore.Set(id, ds);
    }

    [HttpPost("{id}/update")]
    public async Task Update(string id, [FromBody] NodeUpdate? ds = null)
    {
        await nodeSettingsStore.Update(id, ds?.Url);
    }

    [HttpPost("{id}/restart")]
    public async Task Restart(string id)
    {
        await nodeSettingsStore.Restart(id);
    }

    public class NodeUpdate
    {
        public string? Url { get; set; }
    }

    public readonly record struct NodeSettingsDetails(NodeSettings? settings, IList<KeyValuePair<string, string>> details);
}