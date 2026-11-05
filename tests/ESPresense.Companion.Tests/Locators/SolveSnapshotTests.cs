using ESPresense.Locators;
using ESPresense.Models;
using ESPresense.Services;
using MathNet.Numerics.Optimization;
using MathNet.Spatial.Euclidean;
using Microsoft.Extensions.Logging;
using Moq;

namespace ESPresense.Companion.Tests.Locators;

/// <summary>
/// MQTT updates a device's readings on another thread while its scenarios solve. A solver that
/// re-reads them every iteration chases a moving target and can run out of iterations; one whose
/// readings are superseded mid-solve should stop and leave the work to the re-queued pass.
/// </summary>
[TestFixture]
public class SolveSnapshotTests
{
    private ConfigLoader _configLoader = null!;
    private State _state = null!;
    private string _configDir = null!;

    [SetUp]
    public async Task Setup()
    {
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);
        await File.WriteAllTextAsync(Path.Combine(_configDir, "config.yaml"), "floors: []\n");
        _configLoader = new ConfigLoader(_configDir);
        await _configLoader.LoadAsync();
        _state = new State(_configLoader, new NodeTelemetryStore(new Mock<IMqttCoordinator>().Object));
    }

    [TearDown]
    public async Task TearDown()
    {
        await _configLoader.StopAsync(CancellationToken.None);
        _configLoader.Dispose();
        try { Directory.Delete(_configDir, true); } catch { /* ignore */ }
    }

    private Device CreateDevice(int heard, Random r, Floor? floor = null)
    {
        var device = new Device("test-device", null, TimeSpan.FromSeconds(30));
        var truth = new Point3D(8, 6, 1);
        for (var i = 0; i < heard; i++)
        {
            var node = new Node($"n{i}", NodeSourceType.Config);
            var at = new[] { r.NextDouble() * 18, r.NextDouble() * 14, r.NextDouble() * 4 };
            node.Update(_configLoader.Config!, new ConfigNode { Id = node.Id, Name = node.Id, Point = at }, floor == null ? Array.Empty<Floor>() : new[] { floor });
            _state.Nodes[node.Id] = node;
            device.Nodes[node.Id] = new DeviceToNode(device, node)
            {
                Distance = truth.DistanceTo(new Point3D(at[0], at[1], at[2])) * Math.Exp(0.3 * (r.NextDouble() - 0.5)),
                LastHit = DateTime.UtcNow
            };
        }
        return device;
    }

    [Test]
    public void CurrentReadings_AreDetachedFromLaterReadings()
    {
        var device = CreateDevice(3, new Random(1));
        var taken = device.CurrentReadings();
        var before = taken.Select(dn => dn.Distance).ToArray();

        foreach (var dn in device.Nodes.Values) dn.Distance += 5;

        Assert.That(taken.Select(dn => dn.Distance), Is.EqualTo(before));
    }

    [Test]
    public void ReadMessage_BumpsReadingVersion()
    {
        var device = CreateDevice(1, new Random(1));
        var before = device.ReadingVersion;

        device.Nodes.Values.Single().ReadMessage(new DeviceMessage { Distance = 2 });

        Assert.That(device.ReadingVersion, Is.EqualTo(before + 1));
    }

    /// <summary>Takes the readings, then a newer reading lands before the objective is evaluated.</summary>
    private sealed class SupersededMidSolve(Device device) : ILocate
    {
        public int Finished { get; private set; }

        public bool Locate(Scenario scenario)
        {
            scenario.TakeReadings(device);
            device.ReadingArrived();
            scenario.ThrowIfSuperseded();
            Finished++;
            return true;
        }
    }

    [Test]
    public void Locate_Superseded_DropsTheSolveUntilTheGuardLetsOneFinish()
    {
        var device = CreateDevice(3, new Random(1));
        var locator = new SupersededMidSolve(device);
        var scenario = new Scenario(_configLoader.Config, locator, "test");

        for (var i = 0; i < Scenario.MaxConsecutiveAbandons; i++)
            Assert.That(scenario.Locate(), Is.False, $"solve {i + 1} should be dropped");
        Assert.That(locator.Finished, Is.Zero);

        // Readings arriving faster than a solve finishes must not starve the device of a position
        Assert.That(scenario.Locate(), Is.True);
        Assert.That(locator.Finished, Is.EqualTo(1));

        // A finished solve resets the count, so the next superseded one is dropped again
        Assert.That(scenario.Locate(), Is.False);
    }

    [Test]
    public void Locate_NotSuperseded_Finishes()
    {
        var device = CreateDevice(3, new Random(1));
        var scenario = new Scenario(_configLoader.Config, new MultiFloorMultilateralizer(device, _state), "MultiFloor") { Confidence = 0 };

        Assert.That(scenario.Locate(), Is.True);
        Assert.That(scenario.ReasonForExit, Is.Not.EqualTo(ExitCondition.ExceedIterations));
    }

    /// <summary>Called after a locator takes its readings and before it solves: lands a newer reading every time.</summary>
    private sealed class NewReadingOnEveryWeight(Device device) : ESPresense.Weighting.IWeighting
    {
        public double Get(int index, int total)
        {
            device.ReadingArrived();
            return 1;
        }
    }

    private sealed class ErrorCounter : ILogger<MultiFloorMultilateralizer>
    {
        public int Errors;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) Interlocked.Increment(ref Errors);
        }
    }

    private ILocate CreateSolver(string solver, Device d, Floor f, ErrorCounter l) => solver switch
    {
        "NelderMead" => new NelderMeadMultilateralizer(d, f, _state, l) { Weighting = new NewReadingOnEveryWeight(d) },
        "Bfgs" => new BfgsMultilateralizer(d, f, _state, l) { Weighting = new NewReadingOnEveryWeight(d) },
        "Mle" => new MLEMultilateralizer(d, f, _state, l) { Weighting = new NewReadingOnEveryWeight(d) },
        "MultiFloor" => new MultiFloorMultilateralizer(d, _state, l) { Weighting = new NewReadingOnEveryWeight(d) },
        _ => throw new ArgumentOutOfRangeException(nameof(solver))
    };

    // The abort is thrown from inside the objective, so it only works if MathNet's FindMinimum lets it out
    // unwrapped. If it ever wrapped it, the locator's catch-all would log an error and nothing would be dropped.
    [TestCase("NelderMead")]
    [TestCase("Bfgs")]
    [TestCase("Mle")]
    [TestCase("MultiFloor")]
    public void RealSolver_SupersededMidSolve_IsDroppedNotLogged(string solver)
    {
        var floor = new Floor();
        floor.Update(_configLoader.Config!, new ConfigFloor { Id = "f", Name = "f", Bounds = new[] { new[] { -1.0, -1.0, -1.0 }, new[] { 20.0, 16.0, 5.0 } } });
        _state.Floors[floor.Id!] = floor;
        var device = CreateDevice(6, new Random(3), floor);
        var errors = new ErrorCounter();
        var scenario = new Scenario(_configLoader.Config, CreateSolver(solver, device, floor, errors), "f") { Confidence = 0 };
        var start = scenario.Location;

        for (var i = 1; i <= Scenario.MaxConsecutiveAbandons; i++)
        {
            Assert.That(scenario.Locate(), Is.False);
            Assert.That(scenario.ConsecutiveAbandons, Is.EqualTo(i), "the solve should have been dropped, not failed");
            Assert.That(scenario.Location, Is.EqualTo(start), "a dropped solve must not move the scenario");
        }

        // The guard lets the next one finish through the same solver, still under constant new readings
        Assert.That(scenario.Locate(), Is.True);
        Assert.That(scenario.ConsecutiveAbandons, Is.Zero);
        Assert.That(errors.Errors, Is.Zero);
    }
}
