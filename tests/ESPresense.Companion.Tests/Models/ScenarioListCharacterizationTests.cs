using System.Reflection;
using System.Text;
using ESPresense.Locators;
using ESPresense.Models;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Companion.Tests.Models;

/// <summary>
/// Pins the exact scenario list (name, locator type, order) built for a device under every combination of
/// enabled locators. Order matters: MultiScenarioLocator breaks probability/confidence ties by list index.
/// </summary>
public class ScenarioListCharacterizationTests
{
    // Bit order is the order the locators' scenarios are expected in.
    private static readonly string[] Locators = ["nelder_mead", "bfgs", "mle", "multi_floor", "nadaraya_watson", "nearest_node"];

    private static readonly PropertyInfo LocatorProp =
        typeof(Scenario).GetProperty("Locator", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static string Describe(Scenario s) => $"{s.Name}:{LocatorProp.GetValue(s)!.GetType().Name}";

    private static string Yaml(int mask, string? perFloorFilter = null, bool withFloors = true)
    {
        var sb = new StringBuilder();
        if (withFloors)
            sb.Append("""
                floors:
                  - id: f1
                    name: Floor One
                    bounds: [[0, 0, 0], [10, 10, 3]]
                  - id: f2
                    name: Floor Two
                    bounds: [[0, 0, 3], [10, 10, 6]]
                  - id: f3
                    name: Floor Three
                    bounds: [[0, 0, 6], [10, 10, 9]]

                """);
        sb.AppendLine("locators:");
        for (var i = 0; i < Locators.Length; i++)
        {
            sb.AppendLine($"  {Locators[i]}:");
            sb.AppendLine($"    enabled: {((mask & (1 << i)) != 0 ? "true" : "false")}");
            if (perFloorFilter != null && Locators[i] is not ("multi_floor" or "nearest_node"))
                sb.AppendLine($"    floors: {perFloorFilter}");
        }
        return sb.ToString();
    }

    private static List<string> Expected(int mask, IReadOnlyList<string> floorNames)
    {
        var expected = new List<string>();
        bool On(int bit) => (mask & (1 << bit)) != 0;
        if (mask == 0)
        {
            if (floorNames.Count > 0) expected.AddRange(floorNames.Select(f => $"{f}:NelderMeadMultilateralizer"));
            else expected.Add("NearestNode:NearestNode");
            return expected;
        }
        if (On(0)) expected.AddRange(floorNames.Select(f => $"{f}:NelderMeadMultilateralizer"));
        if (On(1)) expected.AddRange(floorNames.Select(f => $"{f}:BfgsMultilateralizer"));
        if (On(2)) expected.AddRange(floorNames.Select(f => $"{f}:MLEMultilateralizer"));
        if (On(3)) expected.Add("MultiFloor:MultiFloorMultilateralizer");
        if (On(4)) expected.AddRange(floorNames.Select(f => $"{f}:NadarayaWatsonMultilateralizer"));
        if (On(5)) expected.Add("NearestNode:NearestNode");
        return expected;
    }

    private static IEnumerable<int> AllMasks() => Enumerable.Range(0, 1 << Locators.Length);

    [TestCaseSource(nameof(AllMasks))]
    public async Task EveryEnabledCombination_AllFloors(int mask)
    {
        await using var fx = await YamlStateFixture.LoadAsync(Yaml(mask));
        // No floor filter: floors come out in the registry's enumeration order, which is what the code has always used.
        var floorNames = fx.State.Floors.Values.Select(f => f.Name!).ToList();
        Assert.That(floorNames, Has.Count.EqualTo(3));

        var device = new Device("dev:1", null, TimeSpan.FromSeconds(30));
        var actual = fx.State.GetScenarios(device).Select(Describe).ToList();

        Assert.That(actual, Is.EqualTo(Expected(mask, floorNames)));
    }

    [TestCase(0b000001)]
    [TestCase(0b111111)]
    [TestCase(0b010110)]
    public async Task FloorFilter_FollowsTheFilterOrderAndSkipsUnknownFloors(int mask)
    {
        await using var fx = await YamlStateFixture.LoadAsync(Yaml(mask, perFloorFilter: "[\"f3\", \"missing\", \"F1\"]"));
        var device = new Device("dev:1", null, TimeSpan.FromSeconds(30));
        var actual = fx.State.GetScenarios(device).Select(Describe).ToList();

        Assert.That(actual, Is.EqualTo(Expected(mask, ["Floor Three", "Floor One"])));
    }

    [Test]
    public async Task NothingEnabled_NoFloors_FallsBackToNearestNode()
    {
        await using var fx = await YamlStateFixture.LoadAsync(Yaml(0, withFloors: false));
        var device = new Device("dev:1", null, TimeSpan.FromSeconds(30));
        Assert.That(fx.State.GetScenarios(device).Select(Describe).ToList(), Is.EqualTo(new[] { "NearestNode:NearestNode" }));
    }

    [Test]
    public async Task NoLocatorsSection_UsesTheConfigDefaults()
    {
        // Config defaults have every locator disabled, so a config with no locators section hits the fallback.
        await using var fx = await YamlStateFixture.LoadAsync("""
            floors:
              - id: f1
                name: Floor One
                bounds: [[0, 0, 0], [10, 10, 3]]
            """);
        var device = new Device("dev:1", null, TimeSpan.FromSeconds(30));
        Assert.That(fx.State.GetScenarios(device).Select(Describe).ToList(), Is.EqualTo(new[] { "Floor One:NelderMeadMultilateralizer" }));
    }

    [Test]
    public async Task AnchoredDevice_GetsOnlyTheAnchorScenario()
    {
        await using var fx = await YamlStateFixture.LoadAsync(Yaml(0b111111));
        var device = new Device("dev:1", null, TimeSpan.FromSeconds(30));
        device.SetAnchor(new DeviceAnchor(new Point3D(1, 2, 0), null, null));

        var scenarios = fx.State.GetScenarios(device).ToList();
        Assert.That(scenarios.Select(Describe), Is.EqualTo(new[] { "Anchored:AnchorLocator" }));
        Assert.That(scenarios[0].Confidence, Is.EqualTo(100));
    }
}
