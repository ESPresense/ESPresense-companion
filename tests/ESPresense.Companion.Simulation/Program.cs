using System;
using System.Collections.Generic;
using ESPresense.Locators;
using ESPresense.Models;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using ESPresense.Simulation;

namespace ESPresense.Simulation.Tests;

/// <summary>
/// Mock ConfigLoader for simulation - no background service needed
/// </summary>
class MockConfigLoader : ConfigLoader
{
    private readonly Config _config;

    public MockConfigLoader(Config config) : base(string.Empty)
    {
        _config = config;
        // Trigger the ConfigChanged event to initialize State
        typeof(ConfigLoader)
            .GetProperty(nameof(Config))!
            .SetValue(this, config);
    }

    public new Config Config => _config;
}

/// <summary>
/// Mock NodeTelemetryStore for simulation - all nodes are considered online
/// </summary>
class MockNodeTelemetryStore : NodeTelemetryStore
{
    public MockNodeTelemetryStore() : base(null!)
    {
    }

    public override bool Online(string id) => true;
    public override NodeTelemetry? Get(string id) => null;
}

/// <summary>
/// Compares actual multilateration algorithms from ESPresense.Companion
/// </summary>
class Program
{
    private const int BaseSeed = 12345;
    private const double SuccessThresholdMeters = 1.0;

    private static (Config Config, Floor Floor, State State, Device Device, MockNodeTelemetryStore NodeTelemetryStore)
        CreateSimulationContext()
    {
        var floor = new Floor();
        var configFloor = new ConfigFloor
        {
            Name = "Test Floor",
            Bounds = new[] { new[] { 0.0, 0.0, 0.0 }, new[] { 12.0, 12.0, 3.0 } }
        };
        var config = new Config { Floors = new[] { configFloor } };
        floor.Update(config, configFloor);

        var mockConfigLoader = new MockConfigLoader(config);
        var nodeTelemetryStore = new MockNodeTelemetryStore();
        var state = new State(mockConfigLoader, nodeTelemetryStore);

        var device = new Device("sim-device", "test-discovery", TimeSpan.FromSeconds(30));
        state.Devices[device.Id] = device;

        return (config, floor, state, device, nodeTelemetryStore);
    }

    private static int SeedFor(string nodeConfigName, string scenarioName)
    {
        return HashCode.Combine(BaseSeed, nodeConfigName, scenarioName);
    }

    static void Main(string[] args)
    {
        Console.WriteLine("ESPresense Multilateration Algorithm Comparison");
        Console.WriteLine("================================================");
        Console.WriteLine("Testing actual ILocate implementations from ESPresense.Companion\n");
        Console.WriteLine($"Accurate run threshold: <= {SuccessThresholdMeters:F1}m 2D error\n");
        
        // Test scenarios
        var scenarios = new (string Name, Action<MultilaterationSimulator> Configure)[]
        {
            ("Perfect Data (no noise)", s => { s.NoiseStdDev = 0; s.ObstacleAbsorption = 0; s.OutlierProbability = 0; }),
            ("Realistic Noise (0.5m std dev)", s => { s.NoiseStdDev = 0.5; s.ObstacleAbsorption = 0; s.OutlierProbability = 0; }),
            ("Heavy Noise (1.5m std dev)", s => { s.NoiseStdDev = 1.5; s.ObstacleAbsorption = 0; s.OutlierProbability = 0; }),
            ("Noise + Outliers (5%)", s => { s.NoiseStdDev = 0.5; s.ObstacleAbsorption = 0; s.OutlierProbability = 0.05; }),
            ("Noise + Obstacles (30% walls)", s => { s.NoiseStdDev = 0.5; s.ObstacleAbsorption = 0.3; s.OutlierProbability = 0; }),
            ("Real World (all effects)", s => { s.NoiseStdDev = 0.5; s.ObstacleAbsorption = 0.3; s.OutlierProbability = 0.05; }),
        };
        
        // Node configurations
        var nodeConfigs = new (string Name, Action<MultilaterationSimulator> Setup)[]
        {
            ("Perimeter (8 nodes)", s => s.GeneratePerimeterNodes(8, 10, 10)),
            ("Grid 4x4 (16 nodes)", s => s.GenerateGridNodes(4, 4, 3.0)),
            ("Sparse (4 nodes)", s => s.GeneratePerimeterNodes(4, 10, 10)),
            ("Collinear (bad)", s => s.GenerateCollinearNodes(6, 2.0)),
        };
        
        // Locators to test
        var locators = new (string Name, Func<Device, Floor, State, NodeTelemetryStore, ILocate> Factory)[]
        {
            ("Gauss-Newton", (d, f, s, nts) => new GaussNewtonMultilateralizer(d, f, s)),
            ("Nelder-Mead", (d, f, s, nts) => new NelderMeadMultilateralizer(d, f, s)),
            ("BFGS", (d, f, s, nts) => new BfgsMultilateralizer(d, f, s)),
            ("MLE", (d, f, s, nts) => new MLEMultilateralizer(d, f, s)),
            ("Nadaraya-Watson", (d, f, s, nts) => new NadarayaWatsonMultilateralizer(d, f, s, nts)),
        };
        
        int iterations = 100;
        
        foreach (var nodeConfig in nodeConfigs)
        {
            Console.WriteLine($"\n\n{'='.ToString().PadRight(60, '=')}");
            Console.WriteLine($"NODE CONFIGURATION: {nodeConfig.Name}");
            Console.WriteLine($"{'='.ToString().PadRight(60, '=')}\n");
            
            foreach (var scenario in scenarios)
            {
                Console.WriteLine($"\n--- {scenario.Name} ---");
                
                foreach (var locatorInfo in locators)
                {
                    try
                    {
                        // Fresh context per algorithm run prevents state leakage across runs.
                        var context = CreateSimulationContext();
                        var seed = SeedFor(nodeConfig.Name, scenario.Name);
                        var sim = new MultilaterationSimulator(context.Floor, context.State, seed);
                        nodeConfig.Setup(sim);
                        scenario.Configure(sim);

                        var locator = locatorInfo.Factory(
                            context.Device,
                            context.Floor,
                            context.State,
                            context.NodeTelemetryStore);
                        var report = sim.RunMonteCarlo(
                            iterations,
                            locator,
                            context.Config,
                            context.Device,
                            SuccessThresholdMeters);
                        report.PrintReport(locatorInfo.Name);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"\n=== {locatorInfo.Name} ===");
                        Console.WriteLine($"ERROR: {ex.Message}");
                    }
                }
            }
        }
        
        Console.WriteLine("\n\nDone!");
    }
}
