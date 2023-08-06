using ESPresense.Models;
using ESPresense.Services;
using Microsoft.AspNetCore.Mvc;

namespace ESPresense.Controllers;

[Route("api/node")]
[ApiController]
public class NodeController : ControllerBase
{
    private readonly ILogger<DeviceController> _logger;
    private readonly NodeSettingsStore _nodeSettingsStore;
    private readonly State _state;

    public NodeController(ILogger<DeviceController> logger, NodeSettingsStore nodeSettingsStore, State state)
    {
        _logger = logger;
        _nodeSettingsStore = nodeSettingsStore;
        _state = state;
    }

    [HttpPut("{id}")]
    public Task Set(string id, [FromBody] NodeSettings ds)
    {
        return _nodeSettingsStore.Set(id, ds);
    }

    [HttpPost("{id}/update")]
    public async Task Set(string id)
    {
        await _nodeSettingsStore.Update(id);
    }
}