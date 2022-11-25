using ESPresense.Models;
using Microsoft.AspNetCore.Mvc;
using SQLite;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace ESPresense.Controllers
{
    [Route("api/rooms")]
    [ApiController]
    public class RoomController : ControllerBase
    {
        private readonly SQLiteConnection _db;
        private readonly ILogger<RoomController> _logger;

        public RoomController(SQLiteConnection db, ILogger<RoomController> logger)
        {
            _db = db;
            _logger = logger;
        }

        // GET: api/rooms
        [HttpGet]
        public IEnumerable<Node> Get()
        {
            return _db.Query<Node>("SELECT * FROM Node");
        }

        // GET api/rooms/5
        [HttpGet("{id}")]
        public string Get(int id)
        {
            return "value";
        }

        // POST api/rooms
        [HttpPost]
        public void Post([FromBody] string value)
        {
        }

        // PUT api/rooms/5
        [HttpPut("{id}")]
        public void Put(int id, [FromBody] string value)
        {
        }

        // DELETE api/rooms/5
        [HttpDelete("{id}")]
        public void Delete(int id)
        {
        }
    }
}
