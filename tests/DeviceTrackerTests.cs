using System.Reflection;
using ESPresense.Controllers;
using ESPresense.Locators;
using ESPresense.Models;
using ESPresense.Services;
using Newtonsoft.Json;

namespace ESPresense.Companion.Tests;

public class DeviceTrackerTests
{
    private IList<TestData> testDatas = new List<TestData>();

    [SetUp]
    public async Task Setup()
    {
        await using var example = Assembly.GetExecutingAssembly().GetManifestResourceStream("ESPresense.Companion.Tests.TestData.stationary,7,14.75,1.25.jsonp") ?? throw new Exception("Could not find embedded stationary,7,14.75,1.25.jsonp");
        using var sr = new StreamReader(example);
        while (true)
        {
            var line = await sr.ReadLineAsync();
            if (line == null) break;
            var testData = JsonConvert.DeserializeObject<TestData>(line);
            if (testData!=null) testDatas.Add(testData);
        }
    }

    [Test]
    public void TestMultiScenarioLocator()
    {
        var configLoader = new ConfigLoader("config");
        var mqtt = new MqttCoordinator(configLoader, null, null, null);
        var locator = new DeviceTracker(new State(configLoader), mqtt, new TelemetryService(mqtt), new GlobalEventDispatcher());
        // Use testData to test locator...
        // Assert.That(result, Is.EqualTo(expectedResult));
    }
}