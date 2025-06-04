using ESPresense.Models;
using ESPresense.Locators;

namespace ESPresense.Companion.Tests;

public class MultiScenarioLocatorTests
{
    private class DummyLocator : ILocate
    {
        public bool Locate(Scenario scenario) => true;
    }

    [Test]
    public void NotHomeStateWhenAllScenariosExpire()
    {
        var device = new Device("id", null, TimeSpan.FromSeconds(1))
        {
            ReportedState = "kitchen"
        };

        var scenario = new Scenario(null, new DummyLocator(), "kitchen")
        {
            LastHit = DateTime.UtcNow.AddSeconds(-5)
        };
        device.Scenarios.Add(scenario);

        var bestScenario = device.Scenarios
            .Where(s => s.Current)
            .OrderByDescending(s => s.Probability)
            .ThenByDescending(s => s.Confidence)
            .ThenBy(s => device.Scenarios.IndexOf(s))
            .FirstOrDefault();

        device.BestScenario = bestScenario;

        if (bestScenario != null)
        {
            var newState = device.Room?.Name ?? device.Floor?.Name ?? "not_home";
            if (newState != device.ReportedState)
            {
                device.ReportedState = newState;
            }
        }
        else if (device.ReportedState != "not_home")
        {
            device.ReportedState = "not_home";
        }

        Assert.That(device.ReportedState, Is.EqualTo("not_home"));
    }
}
