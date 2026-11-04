using ESPresense.Models;
using ESPresense.Services;
using Moq;

namespace ESPresense.Companion.Tests.Services;

public class DeviceEnricherTests
{
    private static (DeviceEnricher, Mock<DeviceSettingsStore>) Create()
    {
        var mqtt = new Mock<IMqttCoordinator>();
        var state = new State(new Mock<ConfigLoader>("test-config-dir").Object, new NodeTelemetryStore(mqtt.Object));
        var dss = new Mock<DeviceSettingsStore>(mqtt.Object, state);
        return (new DeviceEnricher(dss.Object), dss);
    }

    [Test]
    public void Enrich_CopiesConfiguredRefRssiAndName()
    {
        var (enricher, dss) = Create();
        dss.Setup(x => x.Get("tag-1")).Returns(new DeviceSettings { Id = "tag-1", Name = "Friendly Tag", RefRssi = -62 });
        var device = new Device("tag-1", null, TimeSpan.FromSeconds(30)) { Name = "raw-name" };

        enricher.Enrich(device);

        Assert.That(device.ConfiguredRefRssi, Is.EqualTo(-62));
        Assert.That(device.Name, Is.EqualTo("Friendly Tag"));
    }

    [Test]
    public void Enrich_BlankNameKeepsDeviceName_AndMissingSettingsClearConfiguredRefRssi()
    {
        var (enricher, dss) = Create();
        dss.Setup(x => x.Get("tag-1")).Returns(new DeviceSettings { Id = "tag-1", Name = " " });
        var named = new Device("tag-1", null, TimeSpan.FromSeconds(30)) { Name = "raw-name", ConfiguredRefRssi = -70 };
        var unknown = new Device("tag-2", null, TimeSpan.FromSeconds(30)) { Name = "other", ConfiguredRefRssi = -70 };

        enricher.Enrich(new[] { named, unknown });

        Assert.That(named.Name, Is.EqualTo("raw-name"));
        Assert.That(named.ConfiguredRefRssi, Is.Null);
        Assert.That(unknown.Name, Is.EqualTo("other"));
        Assert.That(unknown.ConfiguredRefRssi, Is.Null);
    }
}
