# ESPresense Multilateration Simulator

Compares real `ILocate` implementations from `ESPresense.Companion` under controlled conditions.

## Quick Start

From the repository root:

```bash
dotnet run --project tests/ESPresense.Companion.Simulation/ESPresense.Simulation.csproj
```

Or from this directory:

```bash
cd tests/ESPresense.Companion.Simulation
dotnet run
```

## What It Tests

### Environmental Scenarios

1. **Perfect Data (no noise)**
2. **Realistic Noise (0.5m std dev)**
3. **Heavy Noise (1.5m std dev)**
4. **Noise + Outliers (5%)**
5. **Noise + Obstacles (30% walls)**
6. **Real World (all effects)**

### Node Layouts

1. **Perimeter (8 nodes)**
2. **Grid 4x4 (16 nodes)**
3. **Sparse (4 nodes)**
4. **Collinear (bad)**

### Algorithms Compared

- **Gauss-Newton**
- **Nelder-Mead**
- **BFGS**
- **MLE**
- **Nadaraya-Watson**

### Metrics

- **Solve Rate**: % of runs where the locator returned success and a finite location.
- **Accurate <= 1.0m**: % of all runs with 2D error at or below threshold.
- 2D/3D mean and median error.
- Error spread (std dev, min, max).
- Mean computation time.

## Reproducibility and Fairness

- Each algorithm run uses a fresh `State` and `Device` (no cross-run state leakage).
- Random seed is deterministic per `(node layout, scenario)`.
- Within a given layout/scenario, every algorithm evaluates the same random sample sequence.

## Customizing

Edit `Program.cs`:

- `BaseSeed` for repeatability.
- `SuccessThresholdMeters` for the accuracy pass/fail threshold.
- `iterations` for Monte Carlo run count.
- `scenarios`, `nodeConfigs`, and `locators` arrays.

## Interpreting Results

- Use **Solve Rate** to detect stability/failure behavior.
- Use **Accurate <= threshold** and mean/median error to compare practical quality.
- High solve rate with poor accuracy indicates a solver that converges to bad estimates.
