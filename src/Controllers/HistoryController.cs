using ESPresense.Models;
using Microsoft.AspNetCore.Mvc;

namespace ESPresense.Controllers
{
    [Route("api/history")]
    [ApiController]
    public class HistoryController(DeviceHistoryStore deviceHistory) : ControllerBase
    {
        [HttpGet("{id}")]
        public async Task<DeviceHistoryResponse> Get(string id)
        {
            var history = await deviceHistory.List(id) ?? new List<DeviceHistory>();
            return new DeviceHistoryResponse(history);
        }

        [HttpGet("{id}/range")]
        public async Task<DeviceHistoryResponse> GetRange(string id, [FromQuery] DateTime start, [FromQuery] DateTime end)
        {
            var history = await deviceHistory.List(id, start, end) ?? new List<DeviceHistory>();
            return new DeviceHistoryResponse(history);
        }
    }

    public record DeviceHistoryResponse(IList<DeviceHistory> history);
}
