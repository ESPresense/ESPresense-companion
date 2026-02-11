using System;
using System.Collections.Generic;
using System.Linq;
using ESPresense.Locators;
using ESPresense.Models;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Simulation.Tests;

/// <summary>
/// Tests confidence values when device is on different floors relative to nodes
/// Verifies that confidence is highest when device is on the same floor as nodes
/// </summary>
class MultiFloorConfidenceTest
{
    private const int BaseSeed = 54321;
    private const int IterationsPerScenario = 100;
    
    private static (Config Config, List<Floor> Floors, State State, Device Device, MockNodeTelemetryStore NodeTelemetryStore)
        CreateMultiFloorContext()
    {
        var config = new Config
        {
            Floors = new[]
            {
                new ConfigFloor
                {
                    Id = "basement",
                    Name = "Basement",
                    Bounds = new[] { new[] { 0.0, 0.0, -3.0 }, new[] { 12.0, 12.0, 0.0 } }
                },
                new ConfigFloor
                {
                    Id = "first",
                    Name = "First Floor",
                    Bounds = new[] { new[] { 0.0, 0.0, 0.0 }, new[] { 12.0, 12.0, 3.0 } }
                },
                new ConfigFloor
                {
                    Id = "second",
                    Name = "Second Floor", 
                    Bounds = new[] { new[] { 0.0, 0.0, 3.0 }, new[] { 12.0, 12.0, 6.0 } }
                }
            }
        };

        var floors = new List<Floor>();
        foreach (var cf in config.Floors)
        {
            var floor = new Floor();
            floor.Update(config, cf);
            floors.Add(floor);
        }

        var mockConfigLoader = new MockConfigLoader(config);
        var nodeTelemetryStore = new MockNodeTelemetryStore();
        var state = new State(mockConfigLoader, nodeTelemetryStore);

        var device = new Device("sim-device-multifloor", "test-discovery", TimeSpan.FromSeconds(30));
        state.Devices[device.Id] = device;

        return (config, floors, state, device, nodeTelemetryStore);
    }

    /// <summary>
    /// Create nodes arranged on a specific floor
    /// </summary>
    private static List<Node> CreateNodesOnFloor(Floor floor, double zHeight, int count = 8)
    {
        var nodes = new List<Node>();
        var bounds = floor.Bounds!;
        double width = bounds[1].X - bounds[0].X;
        double depth = bounds[1].Y - bounds[0].Y;
        
        // Create perimeter nodes
        for (int i = 0; i < count; i++)
        {
            double t = (double)i / count;
            double x, y;
            
            if (t < 0.25)
            {
                x = t * 4 * width * 0.8 + width * 0.1;
                y = depth * 0.1;
            }
            else if (t < 0.5)
            {
                x = width * 0.9;
                y = (t - 0.25) * 4 * depth * 0.8 + depth * 0.1;
            }
            else if (t < 0.75)
            {
                x = (0.75 - t) * 4 * width * 0.8 + width * 0.1;
                y = depth * 0.9;
            }
            else
            {
                x = width * 0.1;
                y = (1 - t) * 4 * depth * 0.8 + depth * 0.1;
            }
            
            var node = new Node($"{floor.Id}-node-{i}", NodeSourceType.Config);
            var configNode = new ConfigNode 
            { 
                Name = $"{floor.Id}-node-{i}", 
                Point = new[] { x, y, zHeight },
                Floors = new[] { floor.Id }
            };
            node.Update(new Config(), configNode, new[] { floor });
            nodes.Add(node);
        }
        
        return nodes;
    }

    /// <summary>
    /// Run confidence test for a specific device Z position
    /// </summary>
    private static FloorConfidenceResult TestConfidenceAtZ(
        double deviceZ,
        Floor targetFloor,
        List<Floor> allFloors,
        List<Node> firstFloorNodes,
        List<Node> secondFloorNodes,
        List<Node> basementNodes,
        Config config,
        State state,
        Device device,
        MockNodeTelemetryStore nodeTelemetryStore,
        ILocate locator,
        int iterations,
        int seed)
    {
        var random = new Random(seed);
        var results = new List<SimulationResult>();

        // Get bounds for random position generation
        var bounds = targetFloor.Bounds!;
        double minX = bounds[0].X + 1;
        double maxX = bounds[1].X - 1;
        double minY = bounds[0].Y + 1;
        double maxY = bounds[1].Y - 1;

        for (int i = 0; i < iterations; i++)
        {
            // Random X,Y position within floor bounds
            var truePos = new Point3D(
                random.NextDouble() * (maxX - minX) + minX,
                random.NextDouble() * (maxY - minY) + minY,
                deviceZ
            );

            device.Nodes.Clear();
            
            // Generate readings from all nodes across all floors
            var allNodes = basementNodes.Concat(firstFloorNodes).Concat(secondFloorNodes).ToList();
            
            foreach (var node in allNodes)
            {
                double trueDistance = truePos.DistanceTo(node.Location);
                
                // Add realistic noise
                double noise = GenerateNoise(random, 0.5);
                double measuredDistance = Math.Max(0.1, trueDistance + noise);
                
                var dtn = new DeviceToNode(device, node)
                {
                    Distance = measuredDistance,
                    LastHit = DateTime.UtcNow
                };
                
                device.Nodes[node.Id] = dtn;
            }
            
            var scenario = new Scenario(config, locator, "multifloor-simulation")
            {
                Confidence = 0
            };
            
            bool located = locator.Locate(scenario);
            
            double error2D = Math.Sqrt(
                Math.Pow(scenario.Location.X - truePos.X, 2) +
                Math.Pow(scenario.Location.Y - truePos.Y, 2));

            results.Add(new SimulationResult
            {
                TruePosition = truePos,
                EstimatedPosition = scenario.Location,
                Error = error2D,
                Error3D = scenario.Location.DistanceTo(truePos),
                Confidence = scenario.Confidence ?? 0,
                Located = located,
                Solved = located && IsFinite(scenario.Location)
            });
        }

        return new FloorConfidenceResult
        {
            DeviceZ = deviceZ,
            TargetFloor = targetFloor,
            MeanConfidence = results.Count > 0 ? results.Average(r => r.Confidence) : 0,
            MeanError2D = results.Where(r => r.Solved).Select(r => r.Error).DefaultIfEmpty(0).Average(),
            LocatedCount = results.Count(r => r.Located),
            SuccessRate = (double)results.Count(r => r.Solved && r.Error <= 1.0) / iterations
        };
    }

    private static double GenerateNoise(Random random, double stdDev)
    {
        double u1 = 1.0 - random.NextDouble();
        double u2 = 1.0 - random.NextDouble();
        double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        return stdDev * randStdNormal;
    }

    private static bool IsFinite(Point3D point)
    {
        return !double.IsNaN(point.X) && !double.IsInfinity(point.X) &&
               !double.IsNaN(point.Y) && !double.IsInfinity(point.Y) &&
               !double.IsNaN(point.Z) && !double.IsInfinity(point.Z);
    }

    public static bool Run()
    {
        Console.WriteLine("\n" + "=".PadRight(70, '='));
        Console.WriteLine("MULTI-FLOOR CONFIDENCE SIMULATION");
        Console.WriteLine("=".PadRight(70, '='));
        Console.WriteLine("Testing confidence values when device is on/off the target floor\n");

        var context = CreateMultiFloorContext();
        var config = context.Config;
        var floors = context.Floors;
        var state = context.State;
        var device = context.Device;
        var nodeTelemetryStore = context.NodeTelemetryStore;

        var basement = floors.First(f => f.Id == "basement");
        var first = floors.First(f => f.Id == "first");
        var second = floors.First(f => f.Id == "second");

        // Create nodes on each floor
        var basementNodes = CreateNodesOnFloor(basement, -1.5, 8);
        var firstFloorNodes = CreateNodesOnFloor(first, 1.5, 8);
        var secondFloorNodes = CreateNodesOnFloor(second, 4.5, 8);

        // Add all nodes to state
        foreach (var node in basementNodes.Concat(firstFloorNodes).Concat(secondFloorNodes))
        {
            state.Nodes[node.Id] = node;
        }

        Console.WriteLine("Node Configuration:");
        Console.WriteLine($"  Basement (z=-1.5m): {basementNodes.Count} nodes");
        Console.WriteLine($"  First Floor (z=1.5m): {firstFloorNodes.Count} nodes");
        Console.WriteLine($"  Second Floor (z=4.5m): {secondFloorNodes.Count} nodes");
        Console.WriteLine($"\nIterations per scenario: {IterationsPerScenario}\n");

        // Locators to test
        var locators = new (string Name, Func<Device, Floor, State, NodeTelemetryStore, ILocate> Factory)[]
        {
            ("Gauss-Newton", (d, f, s, nts) => new GaussNewtonMultilateralizer(d, f, s)),
            ("Nelder-Mead", (d, f, s, nts) => new NelderMeadMultilateralizer(d, f, s)),
            ("BFGS", (d, f, s, nts) => new BfgsMultilateralizer(d, f, s)),
            ("MLE", (d, f, s, nts) => new MLEMultilateralizer(d, f, s)),
            ("Nadaraya-Watson", (d, f, s, nts) => new NadarayaWatsonMultilateralizer(d, f, s, nts)),
        };

        bool allPassed = true;

        foreach (var locatorInfo in locators)
        {
            Console.WriteLine("\n" + "-".PadRight(70, '-'));
            Console.WriteLine($"LOCATOR: {locatorInfo.Name}");
            Console.WriteLine("-".PadRight(70, '-'));

            // Test scenarios for First Floor as target
            Console.WriteLine("\n--- Target: First Floor (z=0-3m) ---");

            // Device below target floor (at basement level, but locator targets first floor)
            // Using first floor bounds since the locator always targets first floor
            var belowResult = TestConfidenceAtZ(
                -1.5, first, floors, firstFloorNodes, secondFloorNodes, basementNodes,
                config, state, device, nodeTelemetryStore, locatorInfo.Factory(device, first, state, nodeTelemetryStore),
                IterationsPerScenario, BaseSeed + 1);
            
            var onResult = TestConfidenceAtZ(
                1.5, first, floors, firstFloorNodes, secondFloorNodes, basementNodes,
                config, state, device, nodeTelemetryStore, locatorInfo.Factory(device, first, state, nodeTelemetryStore),
                IterationsPerScenario, BaseSeed + 2);
            
            var aboveResult = TestConfidenceAtZ(
                4.5, first, floors, firstFloorNodes, secondFloorNodes, basementNodes,
                config, state, device, nodeTelemetryStore, locatorInfo.Factory(device, first, state, nodeTelemetryStore),
                IterationsPerScenario, BaseSeed + 3);

            PrintComparison("Device Below (-1.5m)", belowResult, onResult);
            PrintComparison("Device On Floor (1.5m)", onResult, onResult);
            PrintComparison("Device Above (4.5m)", aboveResult, onResult);

            // Verify confidence hierarchy
            bool onFloorHighest = onResult.MeanConfidence >= belowResult.MeanConfidence &&
                                   onResult.MeanConfidence >= aboveResult.MeanConfidence;

            if (!onFloorHighest) allPassed = false;

            Console.WriteLine($"\n  ✓ On-floor confidence highest: {(onFloorHighest ? "PASS" : "FAIL")}");
            Console.WriteLine($"    On: {onResult.MeanConfidence:F1} vs Below: {belowResult.MeanConfidence:F1} vs Above: {aboveResult.MeanConfidence:F1}");
        }

        Console.WriteLine("\n" + "=".PadRight(70, '='));
        Console.WriteLine("SIMULATION COMPLETE");
        Console.WriteLine("=".PadRight(70, '='));

        return allPassed;
    }

    private static void PrintComparison(string label, FloorConfidenceResult result, FloorConfidenceResult baseline)
    {
        Console.WriteLine($"\n  {label}:");
        Console.WriteLine($"    Mean Confidence: {result.MeanConfidence:F1}/100 {(result.MeanConfidence >= baseline.MeanConfidence ? "(≥ baseline)" : "(< baseline)")}");
        Console.WriteLine($"    Mean 2D Error:   {result.MeanError2D:F2}m");
        Console.WriteLine($"    Located:         {result.LocatedCount}/{IterationsPerScenario}");
        Console.WriteLine($"    Success Rate:    {result.SuccessRate:P1}");
    }
}

class FloorConfidenceResult
{
    public double DeviceZ { get; set; }
    public Floor TargetFloor { get; set; } = null!;
    public double MeanConfidence { get; set; }
    public double MeanError2D { get; set; }
    public int LocatedCount { get; set; }
    public double SuccessRate { get; set; }
}
