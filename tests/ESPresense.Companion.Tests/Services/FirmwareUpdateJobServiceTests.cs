using ESPresense.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace ESPresense.Companion.Tests.Services;

public class FirmwareUpdateJobServiceTests
{
    private FirmwareUpdateJobService CreateService()
    {
        var mqtt = new Mock<IMqttCoordinator>();
        var nodeSettingsLogger = new Mock<ILogger<NodeSettingsStore>>();
        var firmwareLogger = new Mock<ILogger<FirmwareUpdateJobService>>();

        var nodeSettingsStore = new NodeSettingsStore(mqtt.Object, nodeSettingsLogger.Object);
        var nodeTelemetryStore = new NodeTelemetryStore(mqtt.Object);

        return new FirmwareUpdateJobService(
            nodeSettingsStore,
            nodeTelemetryStore,
            new HttpClient(),
            firmwareLogger.Object);
    }

    [Test]
    public void IsTrustedFirmwareUrl_AcceptsExpectedHosts()
    {
        var sut = CreateService();

        Assert.That(sut.IsTrustedFirmwareUrl("https://github.com/ESPresense/ESPresense/releases/download/v1/test.bin"), Is.True);
        Assert.That(sut.IsTrustedFirmwareUrl("https://nightly.link/ESPresense/ESPresense/actions/runs/1/test.zip"), Is.True);
    }

    [Test]
    public void IsTrustedFirmwareUrl_RejectsOtherHosts()
    {
        var sut = CreateService();

        Assert.That(sut.IsTrustedFirmwareUrl("https://example.com/firmware.bin"), Is.False);
        Assert.That(sut.IsTrustedFirmwareUrl(""), Is.False);
    }

    [Test]
    public void Start_InvalidUrl_ReturnsError()
    {
        var sut = CreateService();

        var (job, error) = sut.Start("node-1", "https://example.com/firmware.bin");

        Assert.That(job, Is.Null);
        Assert.That(error, Is.EqualTo("Only ESPresense GitHub URLs are allowed"));
    }
}
