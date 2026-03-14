using ESPresense.Extensions;
using ESPresense.Models;
using ESPresense.Services;
using ESPresense.Utils;
using MathNet.Spatial.Euclidean;
using Serilog;

namespace ESPresense.Locators;

/// <summary>
/// Base class for multilateralizer implementations providing common functionality
/// </summary>
public abstract class BaseMultilateralizer : ILocate
{
    protected readonly Device Device;
    protected readonly Floor Floor;
    protected readonly State State;
    protected readonly NodeSettingsStore NodeSettings;
    protected readonly DeviceSettingsStore DeviceSettings;

    protected BaseMultilateralizer(Device device, Floor floor, State state, NodeSettingsStore nodeSettings, DeviceSettingsStore deviceSettings)
    {
        Device = device;
        Floor = floor;
        State = state;
        NodeSettings = nodeSettings;
        DeviceSettings = deviceSettings;
    }

    /// <summary>
    /// Solves for the device position given valid nodes and an initial guess.
    /// Returns null to indicate a soft failure (template will fall back to using the guess with confidence=1).
    /// Implementations may update scenario fields such as Error, Fixes, Iterations, ReasonForExit, and Scale.
    /// </summary>
    protected abstract Point3D? Solve(Scenario scenario, DeviceToNode[] nodes, Point3D guess);

    /// <summary>
    /// Template method: initialises scenario, calls <see cref="Solve"/> in a 3-iteration
    /// gain-correction loop, then finalises confidence and room assignment.
    /// </summary>
    public bool Locate(Scenario scenario)
    {
        if (!InitializeScenario(scenario, out var nodes, out var guess))
            return false;

        int confidence = scenario.Confidence ?? 0;
        var enriched = false;
        try
        {
            if (nodes.Length < 3 || Floor.Bounds == null || Floor.Bounds.Length < 2)
            {
                confidence = 1;
                scenario.UpdateLocation(guess);
            }
            else
            {
                // Enrich nodes with calibration data before iterative solve
                EnrichNodes(nodes);
                enriched = true;

                Point3D? result = null;
                for (int iteration = 0; iteration < 3; iteration++)
                {
                    // On iterations 1+, compute cos(θ) for gain correction
                    if (iteration > 0 && result.HasValue)
                        UpdateNodeCosTheta(nodes, result.Value);

                    result = Solve(scenario, nodes, guess);
                    if (result == null)
                    {
                        confidence = 1;
                        scenario.UpdateLocation(guess);
                        return FinalizeScenario(scenario, confidence);
                    }
                }

                scenario.UpdateLocation(result!.Value);
                CalculateAndSetPearsonCorrelation(scenario, nodes);

                int nodesPossibleOnline = State.Nodes.Values
                    .Count(n => n.Floors?.Contains(Floor) ?? false);

                confidence = MathUtils.CalculateConfidence(
                    scenario.Error,
                    scenario.PearsonCorrelation,
                    nodes.Length,
                    nodesPossibleOnline
                );
            }
        }
        catch (Exception ex)
        {
            confidence = HandleLocatorException(ex, scenario, guess);
        }
        finally
        {
            if (enriched)
                ResetEnrichment(nodes);
        }

        return FinalizeScenario(scenario, confidence);
    }

    private void EnrichNodes(DeviceToNode[] nodes)
    {
        foreach (var dn in nodes)
        {
            // Only activate RSSI-based distance recomputation when real RSSI data is present.
            // Tests and legacy call sites that set Distance directly (without ReadMessage) have Rssi==0;
            // real BLE measurements always have a non-zero RSSI (typically -40 to -100 dBm).
            if (dn.Rssi == 0.0) continue;

            var nodeId = dn.Node?.Id;
            var ns = nodeId != null ? NodeSettings.Get(nodeId) : null;
            var deviceId = dn.Device?.Id;
            var ds = deviceId != null ? DeviceSettings.Get(deviceId) : null;

            dn.NodeAbsorption = ns?.Calibration?.Absorption ?? 3.0;
            dn.NodeRxAdjRssi = ns?.Calibration?.RxAdjRssi ?? 0;

            // Resolve antenna profile from config (profile name → built-in → null).
            // Null means no antenna configured → NodeGMaxDb stays null
            // → CorrectedDistance returns null → Distance falls back to firmware (backward-compatible).
            var configNode = State.Config?.Nodes?.FirstOrDefault(n => n.GetId() == nodeId);
            var antenna = State.Config?.ResolveAntenna(configNode?.Antenna);
            dn.NodeGMaxDb = null;
            dn.NodePatternExponent = 0;
            dn.NodeBackLossDb = 0;
            if (antenna != null)
            {
                dn.NodeGMaxDb = antenna.GMaxDb;
                dn.NodePatternExponent = antenna.PatternExponent;
                dn.NodeBackLossDb = antenna.BackLoss;
                // Use calibrated angles if available, otherwise default to 0°/0° (horizontal, forward)
                double azDeg = ns?.Calibration?.Azimuth ?? 0.0;
                double elDeg = ns?.Calibration?.Elevation ?? 0.0;
                dn.NodeAzimuthRad = azDeg * Math.PI / 180.0;
                dn.NodeElevationRad = elDeg * Math.PI / 180.0;
            }
            else
            {
                dn.NodeAzimuthRad = null;
                dn.NodeElevationRad = null;
            }

            // TxAdjRssi: device transmit adjustment from DeviceSettingsStore
            dn.TxAdjRssi = ds?.RefRssi ?? 0;
        }
    }

    private static void UpdateNodeCosTheta(DeviceToNode[] nodes, Point3D devicePos)
    {
        foreach (var dn in nodes)
        {
            if (dn.NodeAzimuthRad is null || dn.NodeElevationRad is null)
            {
                dn.NodeCosTheta = null;
                continue;
            }

            double azRad = dn.NodeAzimuthRad.Value;
            double elRad = dn.NodeElevationRad.Value;

            // Pointing vector: Px=sin(az)*cos(el), Py=cos(az)*cos(el), Pz=sin(el)
            double px = Math.Sin(azRad) * Math.Cos(elRad);
            double py = Math.Cos(azRad) * Math.Cos(elRad);
            double pz = Math.Sin(elRad);

            // Vector from node to device
            if (dn.Node?.HasLocation != true)
            {
                dn.NodeCosTheta = null;
                continue;
            }
            var nodePos = dn.Node.Location;
            double dx = devicePos.X - nodePos.X;
            double dy = devicePos.Y - nodePos.Y;
            double dz = devicePos.Z - nodePos.Z;
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);

            if (len < 1e-9)
                dn.NodeCosTheta = 1.0; // co-located → θ=0°, max gain
            else
            {
                // Raw signed cosθ: sign determines front vs back hemisphere in CorrectedDistance
                double cosTheta = (px * dx + py * dy + pz * dz) / len;
                dn.NodeCosTheta = cosTheta;
            }
        }
    }

    private static void ResetEnrichment(DeviceToNode[] nodes)
    {
        foreach (var dn in nodes)
        {
            dn.NodeGMaxDb = null;
            dn.NodeCosTheta = null;
            dn.NodeAzimuthRad = null;
            dn.NodeElevationRad = null;
            dn.NodePatternExponent = 0;
            dn.NodeBackLossDb = 0;
        }
    }

    /// <summary>
    /// Initializes scenario with valid nodes and performs initial validation
    /// </summary>
    /// <returns>True if there are enough nodes to proceed with localization</returns>
    protected bool InitializeScenario(Scenario scenario, out DeviceToNode[] nodes, out Point3D guess)
    {
        var confidence = scenario.Confidence ?? 0;

        nodes = Device.Nodes.Values
            .Where(a => a.Current && (a.Node?.Floors?.Contains(Floor) ?? false))
            .Where(a =>
            {
                if (Floor.Bounds == null || Floor.Bounds.Length < 2) return true;
                var z = a.Node?.Location.Z;
                if (!z.HasValue) return false;
                return z.Value >= Floor.Bounds[0].Z && z <= Floor.Bounds[1].Z;
            })
            .OrderBy(a => a.Distance)
            .ToArray();

        var pos = nodes.Select(a => a.Node!.Location).ToArray();

        scenario.Minimum = nodes.Min(a => (double?)a.Distance);
        scenario.LastHit = nodes.Max(a => a.LastHit);
        scenario.Fixes = pos.Length;

        if (pos.Length <= 1)
        {
            ResetScenario(scenario);
            guess = Point3D.NaN;
            return false;
        }

        scenario.Floor = Floor;

        guess = confidence < 5
            ? Point3D.MidPoint(pos[0], pos[1])
            : scenario.Location;

        return true;
    }

    /// <summary>
    /// Resets scenario properties when localization cannot proceed
    /// </summary>
    protected void ResetScenario(Scenario scenario)
    {
        scenario.Room = null;
        scenario.Confidence = 0;
        scenario.Error = null;
        scenario.Floor = null;
    }

    /// <summary>
    /// Clamps a point to within floor bounds
    /// </summary>
    protected Point3D ClampToFloorBounds(Point3D point)
    {
        if (Floor.Bounds == null || Floor.Bounds.Length < 2)
            return point;

        return new Point3D(
            Math.Clamp(point.X, Floor.Bounds[0].X, Floor.Bounds[1].X),
            Math.Clamp(point.Y, Floor.Bounds[0].Y, Floor.Bounds[1].Y),
            Math.Clamp(point.Z, Floor.Bounds[0].Z, Floor.Bounds[1].Z)
        );
    }

    /// <summary>
    /// Calculates and sets Pearson correlation for the scenario
    /// </summary>
    protected void CalculateAndSetPearsonCorrelation(Scenario scenario, DeviceToNode[] nodes)
    {
        if (nodes.Length >= 2)
        {
            var measuredDistances = nodes.Select(dn => dn.Distance).ToList();
            var calculatedDistances = nodes.Select(dn => scenario.Location.DistanceTo(dn.Node!.Location)).ToList();
            scenario.PearsonCorrelation = MathUtils.CalculatePearsonCorrelation(measuredDistances, calculatedDistances);
        }
        else
        {
            scenario.PearsonCorrelation = null;
        }
    }

    /// <summary>
    /// Assigns room to scenario based on location
    /// </summary>
    protected void AssignRoom(Scenario scenario)
    {
        scenario.Room = Floor.Rooms.Values.FirstOrDefault(a =>
            a.Polygon?.EnclosesPoint(scenario.Location.ToPoint2D()) ?? false);
    }

    /// <summary>
    /// Performs final validation and room assignment
    /// </summary>
    /// <returns>True if scenario has moved and is valid</returns>
    protected bool FinalizeScenario(Scenario scenario, int confidence)
    {
        scenario.Confidence = confidence;

        if (confidence <= 0) return false;
        if (Math.Abs(scenario.Location.DistanceTo(scenario.LastLocation)) < 0.1) return false;

        AssignRoom(scenario);
        return true;
    }

    /// <summary>
    /// Handles exceptions during localization
    /// </summary>
    protected int HandleLocatorException(Exception ex, Scenario scenario, Point3D fallbackGuess)
    {
        scenario.UpdateLocation(!double.IsNaN(fallbackGuess.X) ? fallbackGuess : new Point3D());
        Log.Error("Error finding location for {Device}: {Message}", Device, ex.Message);
        return 0;
    }
}
