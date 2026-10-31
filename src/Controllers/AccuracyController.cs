using ESPresense.Models;
using ESPresense.Services;
using Microsoft.AspNetCore.Mvc;

namespace ESPresense.Controllers
{
    [Route("api/accuracy")]
    [ApiController]
    public class AccuracyController(PositionAccuracyService accuracy) : ControllerBase
    {
        [HttpGet]
        public PositionAccuracy Get() => accuracy.Latest;
    }
}
