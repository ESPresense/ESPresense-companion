using ESPresense.Services;
using Microsoft.AspNetCore.Mvc;

namespace ESPresense.Controllers;

[ApiController]
[Route("api/survey")]
public class SurveyController(RobotSurveyService survey, AccuracyService accuracy) : Controller
{
    [HttpGet("status")]
    public object Status() => survey.Status();

    /// <summary>A robot pose in the robot's own frame, for a bridge that reads it from somewhere Companion cannot.</summary>
    [HttpPost("pose")]
    public IActionResult Pose([FromBody] PoseRequest pose)
    {
        if (!double.IsFinite(pose.x) || !double.IsFinite(pose.y)) return BadRequest(new { error = "x and y are required." });
        return survey.PushPose(pose.x, pose.y, pose.heading) ? Accepted() : Conflict(new { error = "The robot survey is not enabled (survey.robot in config.yaml)." });
    }

    [HttpGet("/api/state/accuracy")]
    public object Accuracy() => accuracy.Snapshot();

    /// <summary>Re-scores the survey holdout now rather than at the next interval.</summary>
    [HttpPost("/api/state/accuracy/holdout")]
    public IActionResult Holdout() => accuracy.RequestHoldout() ? Accepted() : Conflict(new { error = "A holdout is already running." });

    public readonly record struct PoseRequest(double x, double y, double? heading);
}
