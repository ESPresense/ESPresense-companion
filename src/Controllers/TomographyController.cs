using ESPresense.Models;
using ESPresense.Services;
using Microsoft.AspNetCore.Mvc;

namespace ESPresense.Controllers;

[Route("api/tomography")]
[ApiController]
public class TomographyController(RadioTomographyService tomography) : ControllerBase
{
    /// <summary>Latest reconstructed per-floor static RF-attenuation map.</summary>
    [HttpGet]
    public TomographyResult Get() => tomography.Latest;
}
