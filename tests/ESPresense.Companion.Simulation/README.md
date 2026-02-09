# ESPresense Multilateration Simulator

Tests and compares multilateration algorithms under various conditions.

## Quick Start

```bash
cd simulation
dotnet run
```

## What It Tests

### Scenarios

1. **Perfect Data** - No noise, ideal conditions (best case)
2. **Realistic Noise** - 0.5m standard deviation Gaussian noise
3. **Heavy Noise** - 1.5m standard deviation (worst realistic case)
4. **Collinear Nodes** - Nodes in a straight line (multilateration nightmare)
5. **Sparse Nodes** - Only 4 nodes (minimum viable)
6. **Dense Nodes** - 16 nodes in grid (redundancy test)

### Algorithms Compared

- **Gauss-Newton** - Iterative least squares
- **Iterative Centroid** - Weighted centroid with error feedback
- **Nelder-Mead** - Simplex optimization
- **BFGS** - Quasi-Newton method
- **MLE** - Maximum Likelihood Estimation
- **Nadaraya-Watson** - Kernel regression
- **Simple Trilateration** - Classic 3-node approach

### Metrics

- Success Rate (% of valid solutions)
- Mean Error (average distance from true position)
- Median Error (typical error)
- Std Dev (consistency)
- Min/Max Error (range)
- Computation Time (performance)

## Customizing Tests

```csharp
var sim = new MultilaterationSimulator(floor);

// Adjust noise
sim.NoiseStdDev = 1.0; // meters

// Add obstacles
sim.ObstacleAbsorption = 0.3; // 30% signal absorption

// Add outliers
sim.OutlierProbability = 0.1; // 10% bad readings
sim.OutlierMultiplier = 3.0; // 3x error

// Generate different node layouts
sim.GenerateGridNodes(4, 4, 3.0);      // 4x4 grid
sim.GeneratePerimeterNodes(8, 10, 10); // 8 nodes on perimeter
sim.GenerateCollinearNodes(6, 2.0);    // 6 nodes in line (bad!)
```

## Adding Your Algorithm

```csharp
static Point3D? MyAlgorithm(List<DeviceToNode> nodes)
{
    // Your multilateration logic here
    // Return estimated position or null if can't solve
}

// Add to GetAlgorithms()
["My Algorithm"] = MyAlgorithm
```

## Interpreting Results

**Best for Real World:**
- High success rate (>95%)
- Low mean error (<1m with 0.5m noise)
- Low std dev (consistent)
- Fast computation (<10ms)

**Red Flags:**
- Fails on collinear nodes (indicates poor conditioning handling)
- Error >> noise std dev (not using data efficiently)
- High variance (unreliable)

## Integration with Companion

To test the actual companion algorithms:

1. Copy the simulator classes to the companion project
2. Reference the real locator implementations
3. Run the same scenarios

```csharp
// Instead of simplified implementations:
var locator = new GaussNewtonMultilateralizer(device, floor, state);
locator.Locate(scenario);
return scenario.Location;
```
