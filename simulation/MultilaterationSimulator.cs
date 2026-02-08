using System;
using System.Collections.Generic;
using System.Linq;
using ESPresense.Locators;
using ESPresense.Models;
using MathNet.Spatial.Euclidean;
using Serilog;

namespace ESPresense.Simulation;

/// <summary>
/// Simulates multilateration scenarios using actual ILocate implementations
/// </summary>
public class MultilaterationSimulator
{
    private readonly Floor _floor;
    private readonly State _state;
    private readonly List<Node> _nodes = new();
    private readonly Random _random = new();
    private readonly int _randomSeed;

    // Simulation parameters
    public double NoiseStdDev { get; set; } = 0.5; // meters
    public double ObstacleAbsorption { get; set; } = 0.3; // 30% longer apparent distance
    public double OutlierProbability { get; set; } = 0.05; // 5% of readings are outliers
    public double OutlierMultiplier { get; set; } = 2.0; // Outliers are 2x distance

    public MultilaterationSimulator(Floor floor, State state, int? seed = null)
    {
        _floor = floor;
        _state = state;
        _randomSeed = seed ?? Random.Shared.Next();
        _random = new Random(_randomSeed);

        // Setup logging to suppress during simulation
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Error()
            .WriteTo.Console()
            .CreateLogger();
    }

    /// <summary>
    /// Reset the simulator state - call between different node configurations
    /// </summary>
    public void Reset()
    {
        _nodes.Clear();
        _state.Nodes.Clear();
        // Re-seed for reproducibility
        _random = new Random(_randomSeed);
    }
    
    /// <summary>
    /// Add a node at specific location
    /// </summary>
    public void AddNode(string id, Point3D location)
    {
        var node = new Node(id, location);
        _nodes.Add(node);
        _state.Nodes[id] = node;
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
    /// Simulate a measurement from device to all nodes using actual ILocate
    /// </summary>
    public SimulationResult Simulate(Point3D truePosition, ILocate locator, Config config)
    {
        // Create FRESH device for each simulation to avoid state accumulation
        var device = new Device($"sim-device-{_random.Next(100000)}", "Simulated Device");
        
        // Generate measurements to all nodes
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
            
            // Create device-to-node reading
            var dtn = new DeviceToNode(device, node)
            {
                Distance = Math.Max(0.1, measuredDistance),
                LastHit = DateTime.UtcNow,
                Current = true
            };
            
            device.Nodes[node.Id] = dtn;
        }
        
        // Create scenario with the locator
        var scenario = new Scenario(config, locator, "simulation");
        
        // Set initial location to centroid of nodes (NOT the true position)
        // This gives iterative solvers a realistic starting point
        var centroid = new Point3D(
            _nodes.Average(n => n.Location.X),
            _nodes.Average(n => n.Location.Y),
            _nodes.Average(n => n.Location.Z)
        );
        scenario.ResetLocation(centroid);
        scenario.Confidence = 0; // Start with no confidence
        
        // Run the actual locator
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        bool moved = locator.Locate(scenario);
        stopwatch.Stop();
        
        var estimatedPosition = scenario.Location;
        double error = estimatedPosition.DistanceTo(truePosition);
        
        return new SimulationResult
        {
            TruePosition = truePosition,
            EstimatedPosition = estimatedPosition,
            Error = error,
            ComputationTimeMs = stopwatch.ElapsedMilliseconds,
            NodeCount = _nodes.Count,
            Fixes = scenario.Fixes ?? 0,
            Confidence = scenario.Confidence ?? 0,
            Iterations = scenario.Iterations ?? 0,
            Moved = moved
        };
    }
    
    /// <summary>
    /// Run Monte Carlo simulation
    /// </summary>
    public SimulationReport RunMonteCarlo(int iterations, ILocate locator, Config config)
    {
        var results = new List<SimulationResult>();
        
        for (int i = 0; i < iterations; i++)
        {
            // Random position on floor
            var truePos = new Point3D(
                _random.NextDouble() * 10 + 1, // 1-11m (avoid edges)
                _random.NextDouble() * 10 + 1,
                1.0 // 1m device height
            );
            
            results.Add(Simulate(truePos, locator, config));
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
    public Point3D EstimatedPosition { get; set; }
    public double Error { get; set; }
    public long ComputationTimeMs { get; set; }
    public int NodeCount { get; set; }
    public int Fixes { get; set; }
    public int Confidence { get; set; }
    public int Iterations { get; set; }
    public bool Moved { get; set; }
}

public class SimulationReport
{
    private readonly List<SimulationResult> _results;
    
    public SimulationReport(List<SimulationResult> results)
    {
        _results = results ?? new List<SimulationResult>();
    }
    
    public int TotalRuns => _results.Count;
    public int SuccessfulRuns => _results.Count(r => r.Fixes >= 3);
    public double SuccessRate => TotalRuns > 0 ? (double)SuccessfulRuns / TotalRuns : 0;
    
    private List<double> ValidErrors => _results.Where(r => r.Fixes >= 3).Select(r => r.Error).ToList();
    
    public double MeanError => ValidErrors.Count > 0 ? ValidErrors.Average() : double.NaN;
    public double MedianError 
    { 
        get 
        {
            var errors = ValidErrors.OrderBy(e => e).ToList();
            if (errors.Count == 0) return double.NaN;
            if (errors.Count % 2 == 1) return errors[errors.Count / 2];
            return (errors[errors.Count / 2 - 1] + errors[errors.Count / 2]) / 2.0;
        }
    }
    public double StdDevError 
    { 
        get 
        {
            var errors = ValidErrors;
            if (errors.Count < 2) return double.NaN;
            var mean = errors.Average();
            return Math.Sqrt(errors.Average(e => Math.Pow(e - mean, 2)));
        }
    }
    public double MaxError => ValidErrors.Count > 0 ? ValidErrors.Max() : double.NaN;
    public double MinError => ValidErrors.Count > 0 ? ValidErrors.Min() : double.NaN;
    public double MeanComputationTimeMs => TotalRuns > 0 ? _results.Average(r => r.ComputationTimeMs) : double.NaN;
    public double MeanFixes => TotalRuns > 0 ? _results.Average(r => r.Fixes) : double.NaN;
    public double MeanIterations => TotalRuns > 0 ? _results.Average(r => r.Iterations) : double.NaN;
    
    public void PrintReport(string algorithmName)
    {
        Console.WriteLine($"\n=== {algorithmName} ===");
        Console.WriteLine($"Success Rate: {SuccessRate:P1} ({SuccessfulRuns}/{TotalRuns})");
        if (SuccessfulRuns > 0)
        {
            Console.WriteLine($"Mean Error: {MeanError:F2}m");
            Console.WriteLine($"Median Error: {MedianError:F2}m");
            if (ValidErrors.Count >= 2)
                Console.WriteLine($"Std Dev: {StdDevError:F2}m");
            Console.WriteLine($"Min/Max Error: {MinError:F2}m / {MaxError:F2}m");
            Console.WriteLine($"Mean Fixes: {MeanFixes:F1}");
            Console.WriteLine($"Mean Iterations: {MeanIterations:F1}");
        }
        else
        {
            Console.WriteLine("No successful runs to report statistics");
        }
        if (!double.IsNaN(MeanComputationTimeMs))
            Console.WriteLine($"Mean Computation Time: {MeanComputationTimeMs:F1}ms");
    }
}
