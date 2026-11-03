using ESPresense.Models;
using ESPresense.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ESPresense.Companion.Tests;

/// <summary>
/// The first load happens in <see cref="ConfigLoader.StartAsync"/> and raises ConfigChanged exactly once.
/// Everything that reads Config relies on having subscribed before that, which only holds because
/// ConfigLoader is the first hosted service registered in Program.cs: the host constructs every hosted
/// service (and its singleton dependency graph) before it calls StartAsync on any of them. These tests
/// pin that contract so reordering Program.cs fails here instead of silently booting with a null Config.
/// </summary>
public class ConfigLoaderStartupOrderingTests
{
    private string _tempDir = null!;

    private const string ValidYaml = "timeout: 45\nmqtt:\n  host: localhost\n";

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "espresense-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Test]
    public async Task SubscribersResolvedFromDiDuringStartupSeeTheInitialConfig()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "config.yaml"), ValidYaml);

        var configLoader = new ConfigLoader(_tempDir);
        var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                // Same shape as Program.cs: the loader is the singleton and the first hosted service,
                // so it is constructed (and starts) before the probe below.
                services.AddSingleton(configLoader);
                services.AddHostedService(_ => configLoader);
                services.AddSingleton<ConfigObserver>();
                services.AddHostedService(sp => sp.GetRequiredService<ConfigObserver>());
            })
            .Build();

        try
        {
            await host.StartAsync();

            var observer = host.Services.GetRequiredService<ConfigObserver>();
            Assert.That(configLoader.Config, Is.Not.Null, "Config must be loaded before StartAsync returns");
            Assert.That(configLoader.Config!.Timeout, Is.EqualTo(45));
            Assert.That(observer.Invocations, Is.EqualTo(1), "the initial load must raise ConfigChanged exactly once");
            Assert.That(observer.Last, Is.SameAs(configLoader.Config), "the subscriber must have received the initial config");
            Assert.That(observer.StartedWithNullConfig, Is.False,
                "a hosted service started after ConfigLoader must not observe a null Config in its constructor");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
            configLoader.Dispose();
        }
    }

    [Test]
    public async Task SingletonResolvedBeforeStartSubscribesBeforeTheInitialLoad()
    {
        // Guards the "subscribe in the constructor, load in StartAsync" split even without the host:
        // this is the ordering Program.cs relies on for State, DeviceHistoryStore, OptimizationRunner, etc.
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "config.yaml"), ValidYaml);

        var configLoader = new ConfigLoader(_tempDir);
        var observer = new ConfigObserver(configLoader);
        Assert.That(observer.CapturedAtConstruction, Is.Null);

        await configLoader.StartAsync(CancellationToken.None);
        try
        {
            Assert.That(observer.Invocations, Is.EqualTo(1));
            Assert.That(observer.CapturedAtConstruction, Is.Not.Null);
            Assert.That(observer.CapturedAtConstruction, Is.SameAs(configLoader.Config));
        }
        finally
        {
            await configLoader.StopAsync(CancellationToken.None);
            configLoader.Dispose();
        }
    }

    /// <summary>
    /// Stand-in for the real singletons (State, DeviceHistoryStore, OptimizationRunner, MqttCoordinator):
    /// subscribes in its constructor and records what it sees.
    /// </summary>
    private sealed class ConfigObserver : IHostedService
    {
        public int Invocations { get; private set; }
        public Config? Last { get; private set; }
        public Config? CapturedAtConstruction { get; private set; }
        public bool StartedWithNullConfig { get; private set; }

        public ConfigObserver(ConfigLoader configLoader)
        {
            configLoader.ConfigChanged += (_, config) =>
            {
                Invocations++;
                Last = config;
                CapturedAtConstruction = config;
            };
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            StartedWithNullConfig = Last == null;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
