using System;
using System.Collections.Generic;
using System.Linq;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Simulation;

/// <summary>
/// Simulates multilateration scenarios to test locator algorithms
/// </summary>
public class MultilaterationSimulator
{
    private readonly Floor _floor;
    private readonly List<Node> _nodes = new();
    private readonly Random _random = new();
    
    // Simulation parameters
    public double NoiseStdDev { get; set; } = 0.5; // meters
    public double ObstacleAbsorption { get; set; } = 0.3; // 30% longer apparent distance
    public double OutlierProbability { get; set; } = 0.05; // 5% of readings are outliers
    public double OutlierMultiplier { get; set; } = 2.0; // Outliers are 2x distance
    
    public MultilaterationSimulator(Floor floor)
    {
        _floor = floor;
    }
    
    /// <summary>
    /// Add a node at specific location
    /// </summary>
    public void AddNode(string id, Point3D location)
    {
        _nodes.Add(new Node { Id = id, Location = location });
    }
    
    /// <summary>
    /// Generate nodes in a grid pattern
    /// </summary>
    public void GenerateGridNodes(int rows, int cols, double spacing)
    {
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                AddNode($"node-{r}-{c}", new Point3D(c * spacing, r * spacing, 2.5)); // 2.5m height
            }
        }
    }
    
    /// <summary>
    /// Generate nodes in a line (collinearity test)
    /// </summary>
    public void GenerateCollinearNodes(int count, double spacing)
    {
        for (int i = 0; i < count; i++)
        {
            AddNode($"node-{i}", new Point3D(i * spacing, 0, 2.5));
        }
    }
    
    /// <summary>
    /// Generate nodes in perimeter (best for multilateration)
    /// </summary>
    public void GeneratePerimeterNodes(int count, double width, double height)
    {
        // Place nodes on perimeter
        for (int i = 0; i < count; i++)
        {
            double t = (double)i / count;
            Point3D loc;
            if (t < 0.25)
                loc = new Point3D(t * 4 * width, 0, 2.5);
            else if (t < 0.5)
                loc = new Point3D(width, (t - 0.25) * 4 * height, 2.5);
            else if (t < 0.75)
                loc = new Point3D((0.75 - t) * 4 * width, height, 2.5);
            else
                loc = new Point3D(0, (1 - t) * 4 * height, 2.5);
            
            AddNode($"node-{i}", loc);
        }
    }
    
    /// <summary>
    /// Simulate a measurement from device to all nodes
    /// </summary>
    public SimulationResult Simulate(Point3D truePosition, Func<List<DeviceToNode>, Point3D?> locateFunc)
    {
        var measurements = new List<DeviceToNode>();
        
        foreach (var node in _nodes)
        {
            double trueDistance = truePosition.DistanceTo(node.Location);
            
            // Apply noise
            double measuredDistance = trueDistance + GenerateNoise();
            
            // Apply obstacle effect (random walls)
            if (_random.NextDouble() < 0.3) // 30% chance of wall
                measuredDistance *= (1 + ObstacleAbsorption);
            
            // Apply outliers
            if (_random.NextDouble() < OutlierProbability)
                measuredDistance *= OutlierMultiplier;
            
            measurements.Add(new DeviceToNode
            {
                Node = node,
                Distance = Math.Max(0.1, measuredDistance), // Min 10cm
                Current = true
            });
        }
        
        // Run the locator
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var estimatedPosition = locateFunc(measurements);
        stopwatch.Stop();
        
        return new SimulationResult
        {
            TruePosition = truePosition,
            EstimatedPosition = estimatedPosition,
            Error = estimatedPosition.HasValue ? truePosition.DistanceTo(estimatedPosition.Value) : double.NaN,
            ComputationTimeMs = stopwatch.ElapsedMilliseconds,
            NodeCount = _nodes.Count,
            Measurements = measurements
        };
    }
    
    /// <summary>
    /// Run Monte Carlo simulation
    /// </summary>
    public SimulationReport RunMonteCarlo(int iterations, Func<List<DeviceToNode>, Point3D?> locateFunc)
    {
        var results = new List<SimulationResult>();
        
        for (int i = 0; i < iterations; i++)
        {
            // Random position on floor
            var truePos = new Point3D(
                _random.NextDouble() * 10, // 10m width
                _random.NextDouble() * 10, // 10m height
                1.0 // 1m device height
            );
            
            results.Add(Simulate(truePos, locateFunc));
        }
        
        return new SimulationReport(results);
    }
    
    private double GenerateNoise()
    {
        // Box-Muller transform for Gaussian noise
        double u1 = 1.0 - _random.NextDouble();
        double u2 = 1.0 - _random.NextDouble();
        double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        return NoiseStdDev * randStdNormal;
    }
}

public class SimulationResult
{
    public Point3D TruePosition { get; set; }
    public Point3D? EstimatedPosition { get; set; }
    public double Error { get; set; }
    public long ComputationTimeMs { get; set; }
    public int NodeCount { get; set; }
    public List<DeviceToNode> Measurements { get; set; } = new();
}

public class SimulationReport
{
    private readonly List<SimulationResult> _results;
    
    public SimulationReport(List<SimulationResult> results)
    {
        _results = results;
    }
    
    public double MeanError => _results.Where(r => !double.IsNaN(r.Error)).Average(r => r.Error);
    public double MedianError => _results.Where(r => !double.IsNaN(r.Error)).OrderBy(r => r.Error).Skip(_results.Count / 2).First().Error;
    public double StdDevError => Math.Sqrt(_results.Where(r => !double.IsNaN(r.Error)).Average(r => Math.Pow(r.Error - MeanError, 2)));
    public double MaxError => _results.Where(r => !double.IsNaN(r.Error)).Max(r => r.Error);
    public double MinError => _results.Where(r => !double.IsNaN(r.Error)).Min(r => r.Error);
    public double SuccessRate => (double)_results.Count(r => !double.IsNaN(r.Error)) / _results.Count;
    public double MeanComputationTimeMs => _results.Average(r => r.ComputationTimeMs);
    
    public void PrintReport(string algorithmName)
    {
        Console.WriteLine($"\n=== {algorithmName} ===");
        Console.WriteLine($"Success Rate: {SuccessRate:P1}");
        Console.WriteLine($"Mean Error: {MeanError:F2}m");
        Console.WriteLine($"Median Error: {MedianError:F2}m");
        Console.WriteLine($"Std Dev: {StdDevError:F2}m");
        Console.WriteLine($"Min/Max Error: {MinError:F2}m / {MaxError:F2}m");
        Console.WriteLine($"Mean Computation Time: {MeanComputationTimeMs:F1}ms");
    }
}

public class Node
{
    public string Id { get; set; } = "";
    public Point3D Location { get; set; }
}

public class DeviceToNode
{
    public Node? Node { get; set; }
    public double Distance { get; set; }
    public bool Current { get; set; }
}

public class Floor
{
    public string? Name { get; set; }
    public Point3D[]? Bounds { get; set; }
}
