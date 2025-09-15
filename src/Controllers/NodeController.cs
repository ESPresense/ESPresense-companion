using ESPresense.Models;
using ESPresense.Services;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace ESPresense.Controllers;

[Route("api/node")]
[ApiController]
public class NodeController(NodeSettingsStore nodeSettingsStore, NodeTelemetryStore nodeTelemetryStore, State state) : ControllerBase
{
    /// <summary>
    /// Retrieve the saved settings for a node and any runtime details available from in-memory state.
    /// </summary>
    /// <param name="id">The node identifier.</param>
    /// <returns>
    /// A <see cref="NodeSettingsDetails"/> containing the stored <see cref="NodeSettings"/> (or a new default with <paramref name="id"/> if none exist)
    /// and a list of key/value detail pairs supplied by the active node in memory (empty if the node is not present or has no details).
    /// </returns>
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
        ds.Id = id;
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

    /// <summary>
    /// Deletes a node and its associated data.
    /// </summary>
    /// <remarks>
    /// Removes persisted node settings and telemetry for the given node id and removes the node from in-memory state.
    /// </remarks>
    /// <param name="id">Identifier of the node to delete.</param>
    /// <returns>HTTP 204 No Content on success.</returns>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        await nodeSettingsStore.Delete(id);
        await nodeTelemetryStore.Delete(id);
        state.Nodes.TryRemove(id, out _);
        return NoContent();
    }

    public class NodeUpdate
    {
        public string? Url { get; set; }
    }

    public readonly record struct NodeSettingsDetails(NodeSettings? settings, IList<KeyValuePair<string, string>> details);
}