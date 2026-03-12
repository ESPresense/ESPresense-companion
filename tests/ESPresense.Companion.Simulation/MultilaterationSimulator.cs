using System;
using System.Collections.Generic;
using System.Linq;
using ESPresense.Locators;
using ESPresense.Models;
using MathNet.Spatial.Euclidean;
using Serilog;
using Serilog.Core;

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
    }

    static MultilaterationSimulator()
    {
        // Setup logging to suppress during simulation (only once)
        if (Log.Logger.GetType().Name == "SilentLogger")
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Error()
                .WriteTo.Console()
                .CreateLogger();
        }
    }

    /// <summary>
    /// Reset the simulator state - call between different node configurations
    /// </summary>
    public void Reset()
    {
        _nodes.Clear();
        _state.Nodes.Clear();
    }
    
    /// <summary>
    /// Add a node at specific location
    /// </summary>
    public void AddNode(string id, Point3D location)
    {
        var node = new Node(id, NodeSourceType.Config);
        // Manually set location using private setter workaround via ConfigNode
        var configNode = new ConfigNode { Name = id, Point = new[] { location.X, location.Y, location.Z } };
        var config = new Config();
        node.Update(config, configNode, new[] { _floor });

        _nodes.Add(node);
        _state.Nodes[id] = node;
    }
    
    /// <summary>
    /// Generate a realistic node height between 0-2m, varied per node
    /// </summary>
    private double NodeHeight(int index)
    {
        // Vary heights: 0.0, 0.5, 1.0, 1.5, 2.0m cycling through
        double[] heights = { 0.0, 0.5, 1.0, 1.5, 2.0, 1.2, 0.8, 1.8 };
        return heights[index % heights.Length];
    }

    /// <summary>
    /// Generate nodes in a grid pattern
    /// </summary>
    public void GenerateGridNodes(int rows, int cols, double spacing)
    {
        int idx = 0;
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                AddNode($"node-{r}-{c}", new Point3D(c * spacing, r * spacing, NodeHeight(idx++)));
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
            AddNode($"node-{i}", new Point3D(i * spacing, 0, NodeHeight(i)));
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
                loc = new Point3D(t * 4 * width, 0, NodeHeight(i));
            else if (t < 0.5)
                loc = new Point3D(width, (t - 0.25) * 4 * height, NodeHeight(i));
            else if (t < 0.75)
                loc = new Point3D((0.75 - t) * 4 * width, height, NodeHeight(i));
            else
                loc = new Point3D(0, (1 - t) * 4 * height, NodeHeight(i));

            AddNode($"node-{i}", loc);
        }
    }
    
    /// <summary>
    /// Simulate a measurement from device to all nodes using actual ILocate
    /// </summary>
    /// <param name="truePosition">The true position of the device</param>
    /// <param name="locator">The locator to test</param>
    /// <param name="config">Configuration</param>
    /// <param name="device">The SAME device instance the locator was constructed with</param>
    public SimulationResult Simulate(Point3D truePosition, ILocate locator, Config config, Device device)
    {
        // Clear previous readings on the locator's own device
        device.Nodes.Clear();
        
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
                LastHit = DateTime.UtcNow
                // Current is a computed property based on LastHit and Device.Timeout
            };

            device.Nodes[node.Id] = dtn;
        }
        
        // Create scenario with the locator
        var scenario = new Scenario(config, locator, "simulation");

        // Do NOT call ResetLocation here - leave the Kalman filter uninitialized.
        // When uninitialized, the first UpdateLocation call from the locator will
        // accept the computed position directly (no filtering). If we Reset first,
        // the near-zero dt between Reset and Update causes the Kalman filter to
        // reject the measurement as "physically impossible movement".
        scenario.Confidence = 0; // Start with no confidence
        
        // Run the actual locator
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        bool located = locator.Locate(scenario);
        stopwatch.Stop();
        
        var estimatedPosition = scenario.Location;
        bool hasFiniteLocation = IsFinite(estimatedPosition);
        bool solved = located && hasFiniteLocation;

        // 2D error (X,Y only) - what matters for indoor positioning
        double error2D = Math.Sqrt(
            Math.Pow(estimatedPosition.X - truePosition.X, 2) +
            Math.Pow(estimatedPosition.Y - truePosition.Y, 2));
        // 3D error for reference
        double error3D = estimatedPosition.DistanceTo(truePosition);

        return new SimulationResult
        {
            TruePosition = truePosition,
            EstimatedPosition = estimatedPosition,
            Error = error2D,
            Error3D = error3D,
            ComputationTimeMs = stopwatch.ElapsedMilliseconds,
            NodeCount = _nodes.Count,
            Fixes = scenario.Fixes ?? 0,
            Confidence = scenario.Confidence ?? 0,
            Iterations = scenario.Iterations ?? 0,
            Located = located,
            Solved = solved
        };
    }
    
    /// <summary>
    /// Run Monte Carlo simulation
    /// </summary>
    public SimulationReport RunMonteCarlo(
        int iterations,
        ILocate locator,
        Config config,
        Device device,
        double successThresholdMeters = 1.0)
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

            results.Add(Simulate(truePos, locator, config, device));
        }
        
        return new SimulationReport(results, successThresholdMeters);
    }
    
    private double GenerateNoise()
    {
        // Box-Muller transform for Gaussian noise
        double u1 = 1.0 - _random.NextDouble();
        double u2 = 1.0 - _random.NextDouble();
        double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        return NoiseStdDev * randStdNormal;
    }

    private static bool IsFinite(Point3D point)
    {
        return !double.IsNaN(point.X) &&
               !double.IsInfinity(point.X) &&
               !double.IsNaN(point.Y) &&
               !double.IsInfinity(point.Y) &&
               !double.IsNaN(point.Z) &&
               !double.IsInfinity(point.Z);
    }
}

public class SimulationResult
{
    public Point3D TruePosition { get; set; }
    public Point3D EstimatedPosition { get; set; }
    public double Error { get; set; }      // 2D (X,Y) error
    public double Error3D { get; set; }    // 3D error for reference
    public long ComputationTimeMs { get; set; }
    public int NodeCount { get; set; }
    public int Fixes { get; set; }
    public int Confidence { get; set; }
    public int Iterations { get; set; }
    public bool Located { get; set; }
    public bool Solved { get; set; }
}

public class SimulationReport
{
    private readonly List<SimulationResult> _results;
    public double SuccessThresholdMeters { get; }
    
    public SimulationReport(List<SimulationResult> results, double successThresholdMeters)
    {
        _results = results ?? new List<SimulationResult>();
        SuccessThresholdMeters = successThresholdMeters;
    }
    
    public int TotalRuns => _results.Count;
    public int SolvedRuns => _results.Count(r => r.Solved);
    public double SolveRate => TotalRuns > 0 ? (double)SolvedRuns / TotalRuns : 0;
    public int SuccessfulRuns => _results.Count(r => r.Solved && r.Error <= SuccessThresholdMeters);
    public double SuccessRate => TotalRuns > 0 ? (double)SuccessfulRuns / TotalRuns : 0;
    
    private List<SimulationResult> SolvedResults => _results.Where(r => r.Solved).ToList();
    private List<double> ValidErrors => SolvedResults.Select(r => r.Error).ToList();
    private List<double> ValidErrors3D => SolvedResults.Select(r => r.Error3D).ToList();

    public double MeanError => ValidErrors.Count > 0 ? ValidErrors.Average() : double.NaN;
    public double MeanError3D => ValidErrors3D.Count > 0 ? ValidErrors3D.Average() : double.NaN;
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
    public double MeanFixes => SolvedRuns > 0 ? SolvedResults.Average(r => r.Fixes) : double.NaN;
    public double MeanIterations => SolvedRuns > 0 ? SolvedResults.Average(r => r.Iterations) : double.NaN;
    
    public void PrintReport(string algorithmName)
    {
        Console.WriteLine($"\n=== {algorithmName} ===");
        Console.WriteLine($"Solve Rate: {SolveRate:P1} ({SolvedRuns}/{TotalRuns})");
        Console.WriteLine($"Accurate <= {SuccessThresholdMeters:F1}m: {SuccessRate:P1} ({SuccessfulRuns}/{TotalRuns})");
        if (SolvedRuns > 0)
        {
            Console.WriteLine($"2D Error (X,Y): {MeanError:F2}m (median {MedianError:F2}m, std {(ValidErrors.Count >= 2 ? StdDevError : 0):F2}m)");
            Console.WriteLine($"3D Error:       {MeanError3D:F2}m");
            Console.WriteLine($"Min/Max 2D:     {MinError:F2}m / {MaxError:F2}m");
            Console.WriteLine($"Fixes: {MeanFixes:F1}  Iterations: {MeanIterations:F1}");
        }
        else
        {
            Console.WriteLine("No solved runs to report statistics");
        }
        if (!double.IsNaN(MeanComputationTimeMs))
            Console.WriteLine($"Mean Computation Time: {MeanComputationTimeMs:F1}ms");
    }
}
