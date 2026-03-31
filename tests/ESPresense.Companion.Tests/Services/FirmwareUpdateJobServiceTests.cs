using ESPresense.Services;
using Microsoft.Extensions.Logging;
using Moq;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;

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
        Assert.That(sut.IsTrustedFirmwareUrl("https://github.com/espresense/ESPresense/releases/download/v1/test.bin"), Is.True);
        Assert.That(sut.IsTrustedFirmwareUrl("https://espresense.com/artifacts/download/runs/123/firmware.bin"), Is.True);
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

    [Test]
    public async Task GetFirmware_EmptyZip_ReturnsReadableStream()
    {
        var mqtt = new Mock<IMqttCoordinator>();
        var nodeSettingsLogger = new Mock<ILogger<NodeSettingsStore>>();
        var firmwareLogger = new Mock<ILogger<FirmwareUpdateJobService>>();

        await using var zipStream = new MemoryStream();
        using (var zipArchive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
        }
        zipStream.Position = 0;

        var httpClient = new HttpClient(new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(zipStream)
        }));
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "tests");

        var sut = new FirmwareUpdateJobService(
            new NodeSettingsStore(mqtt.Object, nodeSettingsLogger.Object),
            new NodeTelemetryStore(mqtt.Object),
            httpClient,
            firmwareLogger.Object);

        var getFirmware = typeof(FirmwareUpdateJobService)
            .GetMethod("GetFirmware", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.That(getFirmware, Is.Not.Null);

        var task = (Task<MemoryStream>)getFirmware!.Invoke(sut, new object[] { "https://github.com/ESPresense/ESPresense/releases/download/v1/test.zip", CancellationToken.None })!;
        await using var result = await task;

        Assert.That(result.CanRead, Is.True);
        Assert.That(result.Length, Is.GreaterThan(0));
    }

    private sealed class StubHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(response);
        }
    }
}
