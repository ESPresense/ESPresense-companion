using ESPresense.Models;
using ESPresense.Services;
using Microsoft.AspNetCore.Mvc;

namespace ESPresense.Controllers;

[Route("api/node")]
[ApiController]
public class NodeController(NodeSettingsStore nodeSettingsStore) : ControllerBase
{
    [HttpPut("{id}")]
    public Task Set(string id, [FromBody] NodeSettings ds)
    {
        return nodeSettingsStore.Set(id, ds);
    }

    [HttpPost("{id}/update")]
    public async Task Update(string id)
    {
        await nodeSettingsStore.Update(id);
    }

    [HttpPost("{id}/restart")]
    public async Task Restart(string id)
    {
        await nodeSettingsStore.Restart(id);
    }
}