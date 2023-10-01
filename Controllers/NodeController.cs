using ESPresense.Models;
using ESPresense.Services;
using Microsoft.AspNetCore.Mvc;

namespace ESPresense.Controllers;

[Route("api/node")]
[ApiController]
public class NodeController : ControllerBase
{
    private readonly NodeSettingsStore _nodeSettingsStore;

    public NodeController(ILogger<DeviceController> logger, NodeSettingsStore nodeSettingsStore, State state, NodeTelemetryStore nts)
    {
        _nodeSettingsStore = nodeSettingsStore;
    }

    [HttpPut("{id}")]
    public Task Set(string id, [FromBody] NodeSettings ds)
    {
        return _nodeSettingsStore.Set(id, ds);
    }

    [HttpPost("{id}/update")]
    public async Task Update(string id)
    {
        await _nodeSettingsStore.Update(id);
    }

    [HttpPost("{id}/restart")]
    public async Task Restart(string id)
    {
        await _nodeSettingsStore.Restart(id);
    }
}