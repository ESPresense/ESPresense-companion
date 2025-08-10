using ESPresense.Models;
using ESPresense.Extensions;

namespace ESPresense.Locators;

internal class NearestNode : ILocate
{
    private readonly Device _device;
    private readonly State _state;
    private string? _currentAssignedNodeId;

    public NearestNode(Device device, State state)
    {
        _device = device;
        _state = state;
    }

    public bool Locate(Scenario scenario)
    {
        var config = _state.Config;
        if (config == null) return false;

        var nearestNodeConfig = config.Locators.NearestNode;

        // 1. Filter nodes by distance and check if they have a valid location
        var potentialNodes = _device.Nodes.Values
            .Where(dtn => dtn.Node != null && dtn.Node.HasLocation) // Ensure node exists and has coordinates
            .Select(dtn => {
                // Use TryGetValue to look up per-node max distance
                double? effectiveMaxDistance = nearestNodeConfig.MaxDistance;
                if (nearestNodeConfig.MaxDistancePerNode != null &&
                    dtn.Node != null &&
                    nearestNodeConfig.MaxDistancePerNode.TryGetValue(dtn.Node.Id, out var nodeMaxDist))
                {
                    effectiveMaxDistance = nodeMaxDist;
                }
                return new { DeviceToNode = dtn, EffectiveMaxDistance = effectiveMaxDistance };
            })
            .Where(x => x.EffectiveMaxDistance == null || x.DeviceToNode.Distance <= x.EffectiveMaxDistance)
            .Select(x => x.DeviceToNode)
            .ToList();

        DeviceToNode? selectedDtn = null;

        if (potentialNodes.Any())
        {
            // 2. Find the best candidate node
            var bestCandidateDtn = potentialNodes.OrderBy(dtn => dtn.Distance).First();

            // 3. Apply Hysteresis
            DeviceToNode? currentAssignedDtn = _currentAssignedNodeId == null ? null : _device.Nodes.GetValueOrDefault(_currentAssignedNodeId);

            // Ensure currentAssignedDtn is still valid (exists in potential nodes)
            if (currentAssignedDtn != null && !potentialNodes.Any(pn => pn.Node?.Id == currentAssignedDtn.Node?.Id))
            {
                currentAssignedDtn = null; // Treat as if no current node if it's out of range now
                _currentAssignedNodeId = null;
            }

            if (currentAssignedDtn == null || currentAssignedDtn.Node == null)
            {
                selectedDtn = bestCandidateDtn;
            }
            else if (bestCandidateDtn.Node.Id == _currentAssignedNodeId)
            {
                selectedDtn = bestCandidateDtn; // Use the latest measurement data for the current node
            }
            else
            {
                // Use TryGetValue to look up per-node hysteresis margin
                double effectiveHysteresis = nearestNodeConfig.HysteresisMargin ?? 0.0;
                if (nearestNodeConfig.HysteresisMarginPerNode != null &&
                    _currentAssignedNodeId != null &&
                    nearestNodeConfig.HysteresisMarginPerNode.TryGetValue(_currentAssignedNodeId, out var nodeHysteresis))
                {
                    effectiveHysteresis = nodeHysteresis;
                }

                if (bestCandidateDtn.Distance < currentAssignedDtn.Distance - effectiveHysteresis)
                {
                    selectedDtn = bestCandidateDtn; // Switch
                }
                else
                {
                    selectedDtn = currentAssignedDtn; // Hold
                }
            }
        }

        // 4. Update Scenario if a node is selected
        if (selectedDtn?.Node != null)
        {
            var nodeLocation = selectedDtn.Node.Location;
            scenario.UpdateLocation(nodeLocation); // Update location using Kalman filter

            // Determine Floor
            var determinedFloor = _state.Floors.Values.FirstOrDefault(f => f.Contained(nodeLocation.Z));

            // Determine Room
            Room? determinedRoom = null;
            if (determinedFloor != null)
            {
                var location2D = nodeLocation.ToPoint2D();
                determinedRoom = determinedFloor.Rooms.Values.FirstOrDefault(r => r.Polygon != null && r.Polygon.EnclosesPoint(location2D));
            }

            var prevRoom = scenario.Room;
            var prevFloor = scenario.Floor;

            scenario.Floor = determinedFloor;
            scenario.Room = determinedRoom;
            scenario.Confidence = 10;
            scenario.Fixes = 1;

            _currentAssignedNodeId = selectedDtn.Node.Id; // Update state for next run

            return scenario.Room != prevRoom || scenario.Floor != prevFloor;
        }
        else
        {
            // No node selected (none in range or hysteresis held on an invalid node)
            scenario.Confidence = 0;
            scenario.Fixes = 0;
            // Clear hysteresis state if the current node is no longer valid
            if (_currentAssignedNodeId != null && !potentialNodes.Any(pn => pn.Node?.Id == _currentAssignedNodeId))
            {
                 _currentAssignedNodeId = null;
            }
            return false; // No valid location found/selected
        }
    }
}