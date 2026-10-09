using ESPresense.Models;
using ESPresense.Services;
using Moq;
using System.Net;
using System.Text;

namespace ESPresense.Companion.Tests.Services;

public class FirmwareCatalogServiceTests
{
    private static FirmwareCatalogService CreateService(string? nodeFirmware = "esp32-verbose", FirmwareTypes? types = null, HttpMessageHandler? handler = null)
    {
        var telemetry = new Mock<NodeTelemetryStore>(Mock.Of<IMqttCoordinator>());
        telemetry.Setup(t => t.Get("node-1")).Returns(new NodeTelemetry { Firmware = nodeFirmware });

        var firmwareTypes = new Mock<IFirmwareTypeStore>();
        firmwareTypes.Setup(f => f.Get()).Returns(types ?? new FirmwareTypes
        {
            Firmware = new List<Firmware>
            {
                new() { Name = "esp32.bin", CPU = "esp32", Flavor = "" },
                new() { Name = "esp32-verbose.bin", CPU = "esp32", Flavor = "verbose" },
                new() { Name = "esp32c3.bin", CPU = "esp32c3", Flavor = "" }
            }
        });

        return new FirmwareCatalogService(handler == null ? new HttpClient() : new HttpClient(handler), firmwareTypes.Object, telemetry.Object);
    }

    [Test]
    public void ResolveFirmwareUrl_Artifact_UsesNodeCurrentFirmware()
    {
        var (url, firmware, error) = CreateService().ResolveFirmwareUrl("node-1", null, 37877747292, null);

        Assert.That(error, Is.Null);
        Assert.That(firmware, Is.EqualTo("esp32-verbose.bin"));
        Assert.That(url, Is.EqualTo("https://espresense.com/artifacts/download/runs/37877747292/esp32-verbose.bin"));
    }

    [Test]
    public void ResolveFirmwareUrl_Release_WithExplicitFirmware()
    {
        var (url, _, error) = CreateService().ResolveFirmwareUrl("node-1", "v4.0.0", null, "esp32c3");

        Assert.That(error, Is.Null);
        Assert.That(url, Is.EqualTo("https://github.com/ESPresense/ESPresense/releases/download/v4.0.0/esp32c3.bin"));
    }

    [TestCase(null, null)]
    [TestCase("v1", 1L)]
    public void ResolveFirmwareUrl_RequiresExactlyOneSource(string? version, long? artifactId)
    {
        var (url, _, error) = CreateService().ResolveFirmwareUrl("node-1", version, artifactId, null);

        Assert.That(url, Is.Null);
        Assert.That(error, Is.EqualTo("Specify exactly one of version or artifactId"));
    }

    [TestCase("../evil")]
    [TestCase("v1/../../x")]
    public void ResolveFirmwareUrl_RejectsInvalidVersion(string version)
    {
        var (url, _, error) = CreateService().ResolveFirmwareUrl("node-1", version, null, null);

        Assert.That(url, Is.Null);
        Assert.That(error, Is.EqualTo("Invalid version"));
    }

    [Test]
    public void ResolveFirmwareUrl_RejectsUnknownFirmware()
    {
        var (url, _, error) = CreateService().ResolveFirmwareUrl("node-1", "v1", null, "../evil.bin");

        Assert.That(url, Is.Null);
        Assert.That(error, Does.StartWith("Unknown firmware"));
    }

    [Test]
    public void ResolveFirmwareUrl_NodeWithoutTelemetry_ReturnsError()
    {
        var (url, _, error) = CreateService(nodeFirmware: null).ResolveFirmwareUrl("node-1", "v1", null, null);

        Assert.That(url, Is.Null);
        Assert.That(error, Does.Contain("has not reported its firmware"));
    }

    [Test]
    public async Task GetArtifactsAsync_FiltersByPullRequestAndBranch_SkippingForks()
    {
        var handler = new RoutingHandler(new()
        {
            ["/artifacts/runs"] = """
                {"workflow_runs":[
                  {"id":3,"head_branch":"main","head_sha":"ccc","head_commit":{"message":"main\nbody"},"created_at":"2026-10-09T04:00:00Z","pull_requests":[],"head_repository":{"full_name":"ESPresense/ESPresense"}},
                  {"id":2,"head_branch":"feat/relay","head_sha":"bbb","head_commit":{"message":"relay\nbody"},"created_at":"2026-10-09T03:06:39Z","pull_requests":[{"number":2530}],"head_repository":{"full_name":"ESPresense/ESPresense"}},
                  {"id":1,"head_branch":"feat/relay","head_sha":"aaa","head_commit":{"message":"fork\nbody"},"created_at":"2026-10-08T03:06:39Z","pull_requests":[{"number":2530}],"head_repository":{"full_name":"someone/ESPresense"}}
                ]}
                """
        });
        var sut = CreateService(handler: handler);

        Assert.That((await sut.GetArtifactsAsync(pullRequest: 2530)).Select(a => a.ArtifactId), Is.EqualTo(new[] { 2L }));
        Assert.That((await sut.GetArtifactsAsync(branch: "main")).Select(a => a.ArtifactId), Is.EqualTo(new[] { 3L }));
        Assert.That((await sut.GetArtifactsAsync(pullRequest: 2530))[0].Title, Is.EqualTo("relay"));
        Assert.That(handler.Requests, Has.Count.EqualTo(1), "listing should be cached");
        Assert.That(handler.UserAgents, Has.All.Not.Empty);
    }

    [Test]
    public async Task GetReleasesAsync_SkipsReleasesWithoutBinariesAndPrerelease()
    {
        var assets = string.Join(",", Enumerable.Repeat("{}", 6));
        var handler = new RoutingHandler(new()
        {
            ["/releases/list"] = $$"""
                [
                  {"tag_name":"v2-beta","name":"v2 beta","prerelease":true,"published_at":"2026-10-01T00:00:00Z","assets":[{{assets}}]},
                  {"tag_name":"v1","name":"v1","prerelease":false,"published_at":"2026-09-01T00:00:00Z","assets":[{{assets}}]},
                  {"tag_name":"v0","name":"v0","prerelease":false,"published_at":null,"assets":[]}
                ]
                """
        });

        var releases = await CreateService(handler: handler).GetReleasesAsync(includePrerelease: false);

        Assert.That(releases.Select(r => r.Version), Is.EqualTo(new[] { "v1" }));
    }

    [Test]
    public void GetReleasesAsync_ListingUnavailable_Throws()
    {
        var handler = new RoutingHandler(new(), new HttpResponseMessage(HttpStatusCode.BadGateway));

        var ex = Assert.ThrowsAsync<HttpRequestException>(() => CreateService(handler: handler).GetReleasesAsync());

        Assert.That(ex!.Message, Does.Contain("502"));
    }

    private sealed class RoutingHandler(Dictionary<string, string> routes, HttpResponseMessage? fallback = null) : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();
        public List<string> UserAgents { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            Requests.Add(uri);
            UserAgents.Add(request.Headers.UserAgent.ToString());

            var path = request.RequestUri!.AbsolutePath;
            var match = routes.FirstOrDefault(r => path.EndsWith(r.Key));
            if (match.Key == null)
                return Task.FromResult(fallback ?? new HttpResponseMessage(HttpStatusCode.NotFound));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(match.Value, Encoding.UTF8, "application/json")
            });
        }
    }
}
