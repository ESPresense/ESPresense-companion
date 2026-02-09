using System;
using System.Collections.Generic;
using ESPresense.Locators;
using ESPresense.Models;
using ESPresense.Services;
using ESPresense.Weighting;
using MathNet.Spatial.Euclidean;
using ESPresense.Simulation;

namespace ESPresense.Simulation.Tests;

/// <summary>
/// Compares different weighting schemes across all locators
/// </summary>
class CompareWeightings
{
    private const int BaseSeed = 12345;
    private const double SuccessThresholdMeters = 1.0;

    private static ConfigWeighting WeightingToConfig(IWeighting weighting)
    {
        return weighting switch
        {
            EqualWeighting => new ConfigWeighting { Algorithm = "equal" },
            LinearWeighting => new ConfigWeighting { Algorithm = "linear" },
            GaussianWeighting g => new ConfigWeighting
            {
                Algorithm = "gaussian",
                Props = g.GetType().GetField("_sigma", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.GetValue(g) is double sigma
                    ? new Dictionary<string, double> { ["sigma"] = sigma }
                    : new Dictionary<string, double>()
            },
            ExponentialWeighting => new ConfigWeighting { Algorithm = "exponential" },
            _ => new ConfigWeighting { Algorithm = "gaussian" }
        };
    }

    private static (Config Config, Floor Floor, State State, Device Device, MockNodeTelemetryStore NodeTelemetryStore)
        CreateSimulationContext(IWeighting? weighting)
    {
        var floor = new Floor();
        var configFloor = new ConfigFloor
        {
            Name = "Test Floor",
            Bounds = new[] { new[] { 0.0, 0.0, 0.0 }, new[] { 12.0, 12.0, 3.0 } }
        };
        var config = new Config { Floors = new[] { configFloor } };

        // Set weighting for all locators if provided
        if (weighting != null)
        {
            var configWeighting = WeightingToConfig(weighting);
            config.Locators = new ConfigLocators
            {
                Bfgs = new BfgsConfig { Weighting = configWeighting },
                NelderMead = new NelderMeadConfig { Weighting = configWeighting },
                Mle = new MleConfig { Weighting = configWeighting }
            };
        }

        floor.Update(config, configFloor);

        var mockConfigLoader = new MockConfigLoader(config);
        var nodeTelemetryStore = new MockNodeTelemetryStore();
        var state = new State(mockConfigLoader, nodeTelemetryStore);

        var device = new Device("sim-device", "test-discovery", TimeSpan.FromSeconds(30));
        state.Devices[device.Id] = device;

        return (config, floor, state, device, nodeTelemetryStore);
    }

    public static void Run()
    {
        Console.WriteLine("ESPresense Weighting Scheme Comparison");
        Console.WriteLine("======================================");
        Console.WriteLine("Comparing: Equal, Linear, Gaussian (σ=0.3), Exponential\n");
        Console.WriteLine($"Accurate run threshold: <= {SuccessThresholdMeters:F1}m 2D error\n");

        // Weighting schemes to test
        var weightings = new (string Name, IWeighting Weighting)[]
        {
            ("Equal", new EqualWeighting()),
            ("Linear", new LinearWeighting(null)),
            ("Gaussian σ=0.3 (default)", new GaussianWeighting(null)),
            ("Gaussian σ=0.1", new GaussianWeighting(new Dictionary<string, double> { ["sigma"] = 0.1 })),
            ("Exponential", new ExponentialWeighting(null)),
        };

        // Test scenarios - focus on realistic noise
        var scenarios = new (string Name, Action<MultilaterationSimulator> Configure)[]
        {
            ("Realistic Noise (0.5m)", s => { s.NoiseStdDev = 0.5; s.ObstacleAbsorption = 0; s.OutlierProbability = 0; }),
            ("Real World (all effects)", s => { s.NoiseStdDev = 0.5; s.ObstacleAbsorption = 0.3; s.OutlierProbability = 0.05; }),
        };

        // Node configurations
        var nodeConfigs = new (string Name, Action<MultilaterationSimulator> Setup)[]
        {
            ("Perimeter (8 nodes)", s => s.GeneratePerimeterNodes(8, 10, 10)),
            ("Sparse (4 nodes)", s => s.GeneratePerimeterNodes(4, 10, 10)),
        };

        // Locators to test (that support weighting)
        var locators = new (string Name, Func<Device, Floor, State, NodeTelemetryStore, ILocate> Factory)[]
        {
            ("BFGS", (d, f, s, nts) => new BfgsMultilateralizer(d, f, s)),
            ("Nelder-Mead", (d, f, s, nts) => new NelderMeadMultilateralizer(d, f, s)),
            ("MLE", (d, f, s, nts) => new MLEMultilateralizer(d, f, s)),
        };

        int iterations = 200;

        foreach (var nodeConfig in nodeConfigs)
        {
            Console.WriteLine($"\n\n{'='.ToString().PadRight(80, '=')}");
            Console.WriteLine($"NODE CONFIGURATION: {nodeConfig.Name}");
            Console.WriteLine($"{'='.ToString().PadRight(80, '=')}\n");

            foreach (var scenario in scenarios)
            {
                Console.WriteLine($"\n--- {scenario.Name} ---");

                foreach (var locatorInfo in locators)
                {
                    Console.WriteLine($"\n  {locatorInfo.Name}:");

                    foreach (var weightingInfo in weightings)
                    {
                        try
                        {
                            var context = CreateSimulationContext(weightingInfo.Weighting);
                            var seed = HashCode.Combine(BaseSeed, nodeConfig.Name, scenario.Name, weightingInfo.Name);
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

                            Console.Write($"    {weightingInfo.Name,-25} ");
                            Console.Write($"Solve: {report.SolveRate,5:P0}  ");
                            Console.Write($"Success: {report.SuccessRate,5:P0}  ");
                            Console.Write($"Mean: {report.MeanError,5:F2}m  ");
                            Console.WriteLine($"Median: {report.MedianError,5:F2}m");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"    {weightingInfo.Name,-25} ERROR: {ex.Message}");
                        }
                    }
                }
            }
        }

        Console.WriteLine("\n\nDone! Look for:");
        Console.WriteLine("  - Highest Accurate rate (% within 1.0m)");
        Console.WriteLine("  - Lowest Mean/Median error");
        Console.WriteLine("  - High Solve rate (stability)");
    }
}
