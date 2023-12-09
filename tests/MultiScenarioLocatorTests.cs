using System.Reflection;
using ESPresense.Locators;
using ESPresense.Models;
using ESPresense.Services;
using Newtonsoft.Json;

namespace ESPresense.Companion.Tests;

public class MultiScenarioLocatorTests
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
        var locator = new MultiScenarioLocator(new State(configLoader), new MqttCoordinator(configLoader, null, null), new DatabaseFactory(null), null);
        // Use testData to test locator...
        // Assert.That(result, Is.EqualTo(expectedResult));
    }
}