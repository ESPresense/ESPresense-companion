using ESPresense.Locators;
using ESPresense.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ESPresense.Utils;

namespace ESPresense.Companion.Tests.Models;

[TestFixture]
public class AreaSwitchTests
{
    private class DummyLocator : ILocate
    {
        public bool Locate(Scenario scenario) => true;
    }

    private static Scenario Scenario(string name, Room? room = null) => new(null, new DummyLocator(), name) { Room = room };

    private static Device Switched(string from, string to, Scenario? best)
    {
        var device = new Device("phone", null, TimeSpan.FromSeconds(30)) { ReportedState = from };
        device.ReportedState = to;
        device.BestScenario = best;
        return device;
    }

    [Test]
    public void SameScenarioMeansThePositionCrossedAWall()
    {
        var scenario = Scenario("nm", new Room());

        var change = AreaSwitch.Describe(Switched("Kitchen", "Hall", scenario), scenario);

        Assert.That(change.From, Is.EqualTo("Kitchen"));
        Assert.That(change.To, Is.EqualTo("Hall"));
        Assert.That(change.Reason, Does.Contain("across a wall"));
    }

    [Test]
    public void ADifferentScenarioMeansALocatorTookOver()
    {
        var (before, after) = (Scenario("a", new Room()), Scenario("b", new Room()));

        var change = AreaSwitch.Describe(Switched("Kitchen", "Hall", after), before);

        Assert.That(change.Reason, Is.EqualTo($"{after.Label} took over from {before.Label}"));
        Assert.That(change.PreviousScenario, Is.EqualTo(before.Label));
    }

    [Test]
    public void AScenarioWithNoRoomMovedOutsideEveryRoom()
    {
        var scenario = Scenario("nm");

        var change = AreaSwitch.Describe(Switched("Kitchen", "Main Floor", scenario), scenario);

        Assert.That(change.Reason, Does.Contain("outside every room"));
    }

    [Test]
    public void NoScenarioMeansNoCurrentFix()
    {
        var change = AreaSwitch.Describe(Switched("Kitchen", "not_home", null), Scenario("nm"));

        Assert.That(change.Reason, Does.StartWith("No current fix"));
        Assert.That(change.Scenario, Is.Null);
    }

    [Test]
    public void ReturningFromNotHomeIsLocatedAgain()
    {
        var scenario = Scenario("nm", new Room());

        var change = AreaSwitch.Describe(Switched("not_home", "Hall", scenario), null);

        Assert.That(change.Reason, Does.Contain("located it again"));
    }

    [Test]
    public void ReasonFitsAnHaState()
    {
        var (before, after) = (Scenario(new string('a', 200), new Room()), Scenario(new string('b', 200), new Room()));

        var change = AreaSwitch.Describe(Switched("Kitchen", "Hall", after), before);

        Assert.That(change.Reason, Has.Length.LessThanOrEqualTo(255));
    }

    [Test]
    public void DiscoveryIsADisabledDiagnosticOnTheSameHaDevice()
    {
        var device = new Device("irk:abc", null, TimeSpan.FromSeconds(30));
        var tracker = device.HassAutoDiscovery.Single();

        var sensor = device.AreaSwitchDiscovery;
        var json = JObject.Parse(JsonConvert.SerializeObject(sensor.Message, SerializerSettings.NullIgnore));

        Assert.That(sensor.Component, Is.EqualTo("sensor"));
        Assert.That(sensor.DiscoveryId, Is.EqualTo($"{tracker.DiscoveryId}_area_switch"));
        Assert.That(json["enabled_by_default"]!.Value<bool>(), Is.False);
        Assert.That(json["entity_category"]!.Value<string>(), Is.EqualTo("diagnostic"));
        Assert.That(json["state_topic"]!.Value<string>(), Is.EqualTo("espresense/companion/irk:abc/area_switch"));
        Assert.That(json["device"]!["identifiers"]!.ToObject<string[]>(), Is.EqualTo(tracker.Message.Device!.Identifiers));
        Assert.That(json["source_type"], Is.Null);
    }
}
