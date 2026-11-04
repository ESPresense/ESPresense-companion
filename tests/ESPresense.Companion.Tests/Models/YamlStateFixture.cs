using ESPresense.Models;
using ESPresense.Services;
using Moq;

namespace ESPresense.Companion.Tests.Models;

/// <summary>
/// A <see cref="State"/> fed by a real <see cref="ConfigLoader"/> reading a YAML string from a temp dir,
/// so tests exercise the same config-load path the app does.
/// </summary>
internal sealed class YamlStateFixture : IAsyncDisposable
{
    public string Dir { get; }
    public ConfigLoader Loader { get; }
    public NodeTelemetryStore Telemetry { get; }
    public State State { get; }

    private YamlStateFixture(string dir, ConfigLoader loader, NodeTelemetryStore nts, State state)
    {
        Dir = dir;
        Loader = loader;
        Telemetry = nts;
        State = state;
    }

    public static async Task<YamlStateFixture> LoadAsync(string yaml)
    {
        var dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "config.yaml"), yaml);
        var loader = new ConfigLoader(dir);
        var nts = new NodeTelemetryStore(new Mock<IMqttCoordinator>().Object);
        var state = new State(loader, nts);
        await loader.LoadAsync();
        Assert.That(state.Config, Is.Not.Null, "config never loaded");
        return new YamlStateFixture(dir, loader, nts, state);
    }

    public async ValueTask DisposeAsync()
    {
        await Loader.StopAsync(CancellationToken.None);
        Loader.Dispose();
        if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
    }
}
