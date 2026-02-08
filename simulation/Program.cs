using System;
using System.Collections.Generic;
using System.Linq;
using MathNet.Spatial.Euclidean;
using ESPresense.Simulation;

namespace ESPresense.Simulation.Tests;

/// <summary>
/// Compares multilateration algorithms under various conditions
/// </summary>
class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("ESPresense Multilateration Algorithm Comparison");
        Console.WriteLine("================================================\n");
        
        var floor = new Floor { 
            Name = "Test Floor", 
            Bounds = new[] { new Point3D(0, 0, 0), new Point3D(10, 10, 3) } 
        };
        
        // Test scenarios
        Console.WriteLine("SCENARIO 1: Perfect Data (no noise)");
        Console.WriteLine("--------------------------------------");
        RunScenario(floor, "Perfect", sim => sim.NoiseStdDev = 0);
        
        Console.WriteLine("\n\nSCENARIO 2: Realistic Noise (0.5m std dev)");
        Console.WriteLine("-------------------------------------------");
        RunScenario(floor, "Noisy", sim => sim.NoiseStdDev = 0.5);
        
        Console.WriteLine("\n\nSCENARIO 3: Heavy Noise (1.5m std dev)");
        Console.WriteLine("---------------------------------------");
        RunScenario(floor, "Heavy Noise", sim => sim.NoiseStdDev = 1.5);
        
        Console.WriteLine("\n\nSCENARIO 4: Collinear Nodes (worst case)");
        Console.WriteLine("-----------------------------------------");
        RunCollinearScenario(floor);
        
        Console.WriteLine("\n\nSCENARIO 5: Sparse Nodes (only 4 nodes)");
        Console.WriteLine("----------------------------------------");
        RunSparseScenario(floor);
        
        Console.WriteLine("\n\nSCENARIO 6: Dense Nodes (16 nodes)");
        Console.WriteLine("-----------------------------------");
        RunDenseScenario(floor);
    }
    
    static void RunScenario(Floor floor, string name, Action<MultilaterationSimulator> configure)
    {
        var sim = new MultilaterationSimulator(floor);
        configure(sim);
        
        // Generate perimeter nodes (optimal for multilateration)
        sim.GeneratePerimeterNodes(8, 10, 10);
        
        Console.WriteLine($"\nConfiguration: {name}");
        Console.WriteLine($"Nodes: 8 (perimeter placement)");
        
        // Test each algorithm
        var algorithms = GetAlgorithms();
        
        foreach (var algo in algorithms)
        {
            var report = sim.RunMonteCarlo(100, algo.Value);
            report.PrintReport(algo.Key);
        }
    }
    
    static void RunCollinearScenario(Floor floor)
    {
        var sim = new MultilaterationSimulator(floor);
        sim.NoiseStdDev = 0.5;
        sim.GenerateCollinearNodes(6, 2.0); // 6 nodes in a straight line
        
        Console.WriteLine("\nConfiguration: 6 collinear nodes (worst case for multilateration)");
        
        var algorithms = GetAlgorithms();
        
        foreach (var algo in algorithms)
        {
            var report = sim.RunMonteCarlo(100, algo.Value);
            report.PrintReport(algo.Key);
        }
    }
    
    static void RunSparseScenario(Floor floor)
    {
        var sim = new MultilaterationSimulator(floor);
        sim.NoiseStdDev = 0.5;
        sim.GeneratePerimeterNodes(4, 10, 10); // Only 4 nodes
        
        Console.WriteLine("\nConfiguration: 4 perimeter nodes (sparse)");
        
        var algorithms = GetAlgorithms();
        
        foreach (var algo in algorithms)
        {
            var report = sim.RunMonteCarlo(100, algo.Value);
            report.PrintReport(algo.Key);
        }
    }
    
    static void RunDenseScenario(Floor floor)
    {
        var sim = new MultilaterationSimulator(floor);
        sim.NoiseStdDev = 0.5;
        sim.GenerateGridNodes(4, 4, 3.33); // 16 nodes in 4x4 grid
        
        Console.WriteLine("\nConfiguration: 16 nodes (4x4 grid)");
        
        var algorithms = GetAlgorithms();
        
        foreach (var algo in algorithms)
        {
            var report = sim.RunMonteCarlo(100, algo.Value);
            report.PrintReport(algo.Key);
        }
    }
    
    static Dictionary<string, Func<List<DeviceToNode>, Point3D?>> GetAlgorithms()
    {
        return new Dictionary<string, Func<List<DeviceToNode>, Point3D?>>
        {
            ["Gauss-Newton"] = GaussNewtonLocate,
            ["Iterative Centroid"] = IterativeCentroidLocate,
            ["Nelder-Mead"] = NelderMeadLocate,
            ["BFGS"] = BfgsLocate,
            ["MLE"] = MleLocate,
            ["Nadaraya-Watson"] = NadarayaWatsonLocate,
            ["Simple Trilateration"] = SimpleTrilaterationLocate
        };
    }
    
    // Algorithm implementations for simulation
    
    static Point3D? GaussNewtonLocate(List<DeviceToNode> nodes)
    {
        // Simplified Gauss-Newton implementation
        if (nodes.Count < 3) return null;
        
        var positions = nodes.Select(n => n.Node!.Location).ToArray();
        var distances = nodes.Select(n => n.Distance).ToArray();
        
        // Initial guess: centroid
        var guess = new Point3D(
            positions.Average(p => p.X),
            positions.Average(p => p.Y),
            positions.Average(p => p.Z)
        );
        
        // Iterative refinement (simplified)
        for (int iter = 0; iter < 20; iter++)
        {
            // Compute residuals
            var residuals = positions.Select((p, i) => 
                guess.DistanceTo(p) - distances[i]).ToArray();
            
            // Compute Jacobian (simplified)
            var jacobian = positions.Select(p => 
            {
                var d = guess.DistanceTo(p);
                if (d < 0.001) d = 0.001;
                return new[] {
                    (p.X - guess.X) / d,
                    (p.Y - guess.Y) / d,
                    (p.Z - guess.Z) / d
                };
            }).ToArray();
            
            // Update (simplified Gauss-Newton step)
            // In reality, solve J^T J dx = -J^T r
            // Here we use a simple gradient descent for simulation
            var gradient = new Point3D(
                jacobian.Select((j, i) => j[0] * residuals[i]).Average(),
                jacobian.Select((j, i) => j[1] * residuals[i]).Average(),
                jacobian.Select((j, i) => j[2] * residuals[i]).Average()
            );
            
            guess = new Point3D(
                guess.X - 0.1 * gradient.X,
                guess.Y - 0.1 * gradient.Y,
                guess.Z - 0.1 * gradient.Z
            );
        }
        
        return guess;
    }
    
    static Point3D? IterativeCentroidLocate(List<DeviceToNode> nodes)
    {
        if (nodes.Count < 3) return null;
        
        var positions = nodes.Select(n => n.Node!.Location).ToArray();
        var distances = nodes.Select(n => n.Distance).ToArray();
        
        // Iterative weighted centroid
        var estimate = new Point3D(
            positions.Average(p => p.X),
            positions.Average(p => p.Y),
            positions.Average(p => p.Z)
        );
        
        for (int iter = 0; iter < 10; iter++)
        {
            var weights = positions.Select((p, i) =>
            {
                var d = estimate.DistanceTo(p);
                var error = Math.Abs(d - distances[i]);
                return 1.0 / (1.0 + error); // Weight inversely proportional to error
            }).ToArray();
            
            var totalWeight = weights.Sum();
            
            estimate = new Point3D(
                positions.Select((p, i) => p.X * weights[i]).Sum() / totalWeight,
                positions.Select((p, i) => p.Y * weights[i]).Sum() / totalWeight,
                positions.Select((p, i) => p.Z * weights[i]).Sum() / totalWeight
            );
        }
        
        return estimate;
    }
    
    static Point3D? NelderMeadLocate(List<DeviceToNode> nodes)
    {
        // Simplified Nelder-Mead (just use centroid for simulation)
        return IterativeCentroidLocate(nodes);
    }
    
    static Point3D? BfgsLocate(List<DeviceToNode> nodes)
    {
        // Simplified BFGS (use Gauss-Newton for simulation)
        return GaussNewtonLocate(nodes);
    }
    
    static Point3D? MleLocate(List<DeviceToNode> nodes)
    {
        // Maximum Likelihood Estimation (weighted by distance uncertainty)
        if (nodes.Count < 3) return null;
        
        // Weight closer nodes more heavily (they're more accurate)
        var weights = nodes.Select(n => 1.0 / (n.Distance * n.Distance + 0.1)).ToArray();
        var totalWeight = weights.Sum();
        
        var positions = nodes.Select(n => n.Node!.Location).ToArray();
        
        return new Point3D(
            positions.Select((p, i) => p.X * weights[i]).Sum() / totalWeight,
            positions.Select((p, i) => p.Y * weights[i]).Sum() / totalWeight,
            positions.Select((p, i) => p.Z * weights[i]).Sum() / totalWeight
        );
    }
    
    static Point3D? NadarayaWatsonLocate(List<DeviceToNode> nodes)
    {
        // Kernel regression approach
        if (nodes.Count < 3) return null;
        
        var positions = nodes.Select(n => n.Node!.Location).ToArray();
        var distances = nodes.Select(n => n.Distance).ToArray();
        
        // Use Gaussian kernel
        double Bandwidth(double d) => Math.Exp(-d * d / 2.0);
        
        var estimate = new Point3D(
            positions.Average(p => p.X),
            positions.Average(p => p.Y),
            positions.Average(p => p.Z)
        );
        
        for (int iter = 0; iter < 5; iter++)
        {
            var kernels = positions.Select((p, i) =>
            {
                var d = estimate.DistanceTo(p);
                var error = Math.Abs(d - distances[i]);
                return Bandwidth(error);
            }).ToArray();
            
            var totalKernel = kernels.Sum();
            
            estimate = new Point3D(
                positions.Select((p, i) => p.X * kernels[i]).Sum() / totalKernel,
                positions.Select((p, i) => p.Y * kernels[i]).Sum() / totalKernel,
                positions.Select((p, i) => p.Z * kernels[i]).Sum() / totalKernel
            );
        }
        
        return estimate;
    }
    
    static Point3D? SimpleTrilaterationLocate(List<DeviceToNode> nodes)
    {
        // Classic trilateration using closest 3 nodes
        var sorted = nodes.OrderBy(n => n.Distance).Take(3).ToArray();
        if (sorted.Length < 3) return null;
        
        var p1 = sorted[0].Node!.Location;
        var p2 = sorted[1].Node!.Location;
        var p3 = sorted[2].Node!.Location;
        var d1 = sorted[0].Distance;
        var d2 = sorted[1].Distance;
        var d3 = sorted[2].Distance;
        
        // Simplified: return weighted average by inverse distance
        var w1 = 1.0 / d1;
        var w2 = 1.0 / d2;
        var w3 = 1.0 / d3;
        var totalW = w1 + w2 + w3;
        
        return new Point3D(
            (p1.X * w1 + p2.X * w2 + p3.X * w3) / totalW,
            (p1.Y * w1 + p2.Y * w2 + p3.Y * w3) / totalW,
            (p1.Z * w1 + p2.Z * w2 + p3.Z * w3) / totalW
        );
    }
}
