# ESPresense Multilateration Simulator

Compares real `ILocate` implementations from `ESPresense.Companion` under controlled conditions.
Also exposes those same implementations as a stdin/stdout JSON CLI so external accuracy
harnesses (e.g. firmware QA) can call them without taking a process-level dependency on
the full Companion service stack.

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

## Subcommands

- (no args) — Monte-Carlo comparison across scenarios and node layouts.
- `weightings` — compare weighting schemes for the chosen locator.
- `multifloor` — multi-floor confidence regression test.
- `locate` — read one JSON solve request on stdin, write one solve result on stdout.

### `locate` CLI

Reads a single JSON request describing the floor, the chosen locator, the stations,
and the per-station distance measurements. Emits a single JSON solve on stdout.

```bash
dotnet run --project tests/ESPresense.Companion.Simulation -- locate \
  < tests/ESPresense.Companion.Simulation/locate-example.json
```

Request shape (snake_case):

```json
{
  "floor_bounds": [[0, 0, 0], [12, 12, 3]],
  "locator": "NelderMead",
  "stations": [{ "id": "n0", "x": 0, "y": 0, "z": 1 }],
  "distances": [{ "station_id": "n0", "distance_m": 7.07 }]
}
```

Supported `locator` values: `NelderMead`, `GaussNewton`, `BFGS`, `MLE`
(case-insensitive; `nelder-mead` and `gauss-newton` also accepted).
`NadarayaWatson` is intentionally not exposed here because it depends on
node telemetry data that an offline harness does not have.

Response shape:

```json
{
  "x": 6.0, "y": 6.0, "z": 1.0,
  "confidence": 7,
  "fixes": 4,
  "error": 0.0123,
  "iterations": 42,
  "reason_for_exit": "BoundTolerance",
  "moved": true
}
```

Exit codes: `0` solve completed (whether or not `moved` is true);
`1` locator threw; `2` invalid or empty input.

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
