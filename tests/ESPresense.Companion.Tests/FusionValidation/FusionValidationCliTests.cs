using ESPresense.Simulation.FusionValidation;

namespace ESPresense.Companion.Tests.FusionValidation;

[TestFixture]
public class FusionValidationCliTests
{
    [Test]
    public void Run_MissingHaHistoryFile_ReturnsNonZero_InsteadOfThrowing()
    {
        // File.ReadAllText throws FileNotFoundException on a missing --ha-history path; before this
        // fix only ArgumentException was caught, so this escaped as an unhandled exception with a
        // stack trace instead of a short message and exit code.
        var exit = -1;
        Assert.DoesNotThrow(() => exit = FusionValidationCli.Run(new[]
        {
            "--ha-history", "/nonexistent/ha-history.json",
            "--fusion-events", "/nonexistent/fusion-events.json"
        }));

        Assert.That(exit, Is.EqualTo(1));
    }
}
