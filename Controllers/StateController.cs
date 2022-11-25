using ESPresense.Models;
using Microsoft.AspNetCore.Mvc;
using SQLite;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace ESPresense.Controllers
{
    [Route("api/state")]
    [ApiController]
    public class StateController : ControllerBase
    {
        private readonly SQLiteConnection _db;
        private readonly ILogger<RoomController> _logger;
        private readonly State _state;

        public StateController(SQLiteConnection db, ILogger<RoomController> logger, State state)
        {
            _db = db;
            _logger = logger;
            _state = state;
        }

        // GET: api/rooms
        [HttpGet("nodes")]
        public IEnumerable<Node> GetNodes()
        {
            return _state.Nodes.Values;
        }

        
        // GET: api/rooms
        [HttpGet("devices")]
        public IEnumerable<Device> GetDevices()
        {
            return _state.Devices.Values;
        }
    }
}
