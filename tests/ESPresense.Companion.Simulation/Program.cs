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
        // Check if user wants weighting comparison
        if (args.Length > 0 && args[0] == "weightings")
        {
            CompareWeightings.Run();
            return;
        }

        // Check if user wants multi-floor confidence test
        if (args.Length > 0 && args[0] == "multifloor")
        {
            bool passed = MultiFloorConfidenceTest.Run();
            Environment.Exit(passed ? 0 : 1);
            return;
        }

        if (args.Length > 0 && args[0] == "locate")
        {
            int exit = LocateCli.Run(Console.In, Console.Out, Console.Error);
            Environment.Exit(exit);
            return;
        }

        if (args.Length > 0 && args[0] == "accuracy")
        {
            int exit = ESPresense.Simulation.AccuracyHarness.AccuracyCli.Run(args.Skip(1).ToArray());
            Environment.Exit(exit);
            return;
        }

        if (args.Length > 0 && args[0] == "replay")
        {
            int exit = ReplayCli.Run(args.Skip(1).ToArray(), Console.Out, Console.Error);
            Environment.Exit(exit);
            return;
        }

        if (args.Length > 0 && args[0] == "calval")
        {
            int exit = ReplayCli.CalVal(args.Skip(1).ToArray(), Console.Out, Console.Error);
            Environment.Exit(exit);
            return;
        }

        if (args.Length > 0 && args[0] == "fusion-validate")
        {
            int exit = ESPresense.Simulation.FusionValidation.FusionValidationCli.Run(args.Skip(1).ToArray());
            Environment.Exit(exit);
            return;
        }

        if (args.Length > 0 && args[0] == "fusion-fixtures")
        {
            var outDir = args.Length > 1 ? args[1] : "FusionValidation/Fixtures";
            System.IO.Directory.CreateDirectory(outDir);
            var (history, events, flap) = ESPresense.Simulation.FusionValidation.SyntheticFixtureBuilder.Build();
            var haHistoryPath = System.IO.Path.Combine(outDir, "ha-history.json");
            var fusionEventsPath = System.IO.Path.Combine(outDir, "fusion-events.json");
            ESPresense.Simulation.FusionValidation.HaHistoryClient.SaveToFile(haHistoryPath, history);
            ESPresense.Simulation.FusionValidation.FusionEventLog.SaveToFile(fusionEventsPath, events);

            // Score it immediately against the flap window the builder itself knows about, rather than
            // leaving `fusion-validate --flap ...` as a step a reader has to reverse-engineer: the
            // committed report.json/.md are reproducible verbatim with just `fusion-fixtures`.
            var excluded = new HashSet<string> { "binary_sensor.family_occupancy" };
            var groundTruth = ESPresense.Simulation.FusionValidation.GroundTruthBuilder.Build(
                history, excluded, ESPresense.Simulation.FusionValidation.FusionValidationCli.RoomFromOccupancyEntity, state => state);
            var report = ESPresense.Simulation.FusionValidation.ValidationMetrics.Compute(
                events, groundTruth, new[] { flap }, TimeSpan.FromMinutes(10));

            var reportJsonPath = System.IO.Path.Combine(outDir, "report.json");
            var reportMdPath = System.IO.Path.Combine(outDir, "report.md");
            System.IO.File.WriteAllText(reportJsonPath, ESPresense.Simulation.FusionValidation.FusionValidationReportWriter.ToJson(report));
            System.IO.File.WriteAllText(reportMdPath, ESPresense.Simulation.FusionValidation.FusionValidationReportWriter.ToMarkdown(report));

            Console.WriteLine($"Wrote {haHistoryPath}, {fusionEventsPath}, {reportJsonPath}, {reportMdPath}");
            Console.WriteLine($"({events.Count} fusion events; synthetic fixture -- not live HA history. Flap window scored: {flap.DeviceId} {flap.Start:O}..{flap.End:O})");
            return;
        }

        Console.WriteLine("ESPresense Multilateration Algorithm Comparison");
        Console.WriteLine("================================================");
        Console.WriteLine("Testing actual ILocate implementations from ESPresense.Companion\n");
        Console.WriteLine($"Accurate run threshold: <= {SuccessThresholdMeters:F1}m 2D error\n");
        Console.WriteLine("Tips:");
        Console.WriteLine("  Run 'dotnet run weightings' to compare weighting schemes");
        Console.WriteLine("  Run 'dotnet run multifloor' to test multi-floor confidence");
        Console.WriteLine("  Pipe a JSON request to 'dotnet run locate' to call ILocate as a CLI");
        Console.WriteLine("  Run 'dotnet run accuracy baseline' for the public accuracy baseline (real ILocate)");
        Console.WriteLine("  Run 'dotnet run fusion-validate --ha-history FILE --fusion-events FILE' to score CSI+BLE fusion output against HA history (ESPA-463)");
        Console.WriteLine("  Run 'dotnet run fusion-fixtures [outDir]' to generate a synthetic ha-history/fusion-events pair from the real CsiFusionService\n");
        
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
