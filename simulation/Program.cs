using System;
using System.Collections.Generic;
using ESPresense.Locators;
using ESPresense.Models;
using MathNet.Spatial.Euclidean;
using ESPresense.Simulation;

namespace ESPresense.Simulation.Tests;

/// <summary>
/// Compares actual multilateration algorithms from ESPresense.Companion
/// </summary>
class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("ESPresense Multilateration Algorithm Comparison");
        Console.WriteLine("================================================");
        Console.WriteLine("Testing actual ILocate implementations from ESPresense.Companion\n");
        
        // Setup floor and state
        var floor = new Floor("Test Floor", new[] { new Point3D(0, 0, 0), new Point3D(12, 12, 3) });
        var state = new State();
        
        // Setup config
        var config = new Config();
        
        // Create test device (needed for locators)
        var device = new Device("sim-device", "Test Device");
        state.Devices["sim-device"] = device;
        
        // Test scenarios
        var scenarios = new (string Name, Action<MultilaterationSimulator> Configure)[]
        {
            ("Perfect Data (no noise)", s => s.NoiseStdDev = 0),
            ("Realistic Noise (0.5m std dev)", s => s.NoiseStdDev = 0.5),
            ("Heavy Noise (1.5m std dev)", s => s.NoiseStdDev = 1.5),
            ("Noisy with Outliers (5% outliers)", s => { s.NoiseStdDev = 0.5; s.OutlierProbability = 0.05; }),
            ("Obstacles (30% walls)", s => { s.NoiseStdDev = 0.5; s.ObstacleAbsorption = 0.3; }),
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
        var locators = new (string Name, Func<Device, Floor, State, ILocate> Factory)[]
        {
            ("Gauss-Newton", (d, f, s) => new GaussNewtonMultilateralizer(d, f, s)),
            ("Nelder-Mead", (d, f, s) => new NelderMeadMultilateralizer(d, f, s)),
            ("BFGS", (d, f, s) => new BfgsMultilateralizer(d, f, s)),
            ("Iterative Centroid", (d, f, s) => new IterativeCentroidMultilateralizer(d, f, s)),
            ("MLE", (d, f, s) => new MLEMultilateralizer(d, f, s)),
            ("Nadaraya-Watson", (d, f, s) => new NadarayaWatsonMultilateralizer(d, f, s)),
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

                var sim = new MultilaterationSimulator(floor, state, seed: 12345); // Fixed seed for reproducibility
                nodeConfig.Setup(sim);
                scenario.Configure(sim);
                
                foreach (var locatorInfo in locators)
                {
                    try
                    {
                        // Create fresh locator for each test
                        var locator = locatorInfo.Factory(device, floor, state);
                        var report = sim.RunMonteCarlo(iterations, locator, config);
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
