using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ESPresense.Controllers;
using ESPresense.Events;
using ESPresense.Models;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Microsoft.Extensions.Logging;
using MQTTnet;

namespace ESPresense.Simulation.FusionValidation;

/// <summary>
/// Drives the real <see cref="CsiFusionService"/>/<see cref="CsiMotionTracker"/> through a synthetic
/// timeline encoding the three reference cases named in ESPA-463, and records the real
/// <see cref="RoomOccupancy"/> output (via <see cref="FusionRoomEvent.FromRoomOccupancy"/>) plus a
/// matching synthetic HA history, so <c>fusion-validate</c> can score real fusion-engine behaviour
/// instead of hand-authored events.
///
/// NOT live HA/firmware data -- there is no network access to a real Home Assistant instance in this
/// environment. The timeline below reproduces the documented *shapes* (flap cadence, CSI-leads-HA gap,
/// office false-alarm) from ESPA-463, not actual recorded history.
/// </summary>
public static class SyntheticFixtureBuilder
{
    public static (Dictionary<string, List<HaStateChange>> History, List<FusionRoomEvent> Events) Build()
    {
        var baseTime = new DateTimeOffset(2026, 11, 3, 18, 0, 0, TimeSpan.Zero);
        var baseUtc = baseTime.UtcDateTime;

        var config = new Config();
        var floor = new Floor();
        floor.Update(config, new ConfigFloor
        {
            Id = "first",
            Name = "first",
            Bounds = new[] { new[] { 0.0, 0, 0 }, new[] { 20.0, 20, 3 } },
            Rooms = new[]
            {
                Square("Laundry", 0, 0, 4, 4),
                Square("Family", 6, 0, 10, 4),
                Square("Office", 12, 0, 16, 4)
            }
        });

        var mqtt = new NullMqttCoordinator();
        var mockConfigLoader = new MockConfigLoaderForFusion(config);
        var nodeTelemetryStore = new MockNodeTelemetryStoreForFusion();
        var state = new State(mockConfigLoader, nodeTelemetryStore);
        state.Floors["first"] = floor;

        var laundryNode = AddNode(state, config, "laundry-node", new Point3D(1, 1, 1), floor);
        var familyNode = AddNode(state, config, "family-node", new Point3D(7, 1, 1), floor);
        var officeNode = AddNode(state, config, "office-node", new Point3D(13, 1, 1), floor);

        var tracker = new CsiMotionTracker(mqtt);
        var fusion = new CsiFusionService(state, tracker, mqtt, new GlobalEventDispatcher(), Microsoft.Extensions.Logging.Abstractions.NullLogger<CsiFusionService>.Instance);

        var laundryRoom = floor.Rooms["laundry"];
        var familyRoom = floor.Rooms["family"];

        var laundryScenario = new Scenario(null, null!, "sim") { Room = laundryRoom, Probability = 0.5, LastHit = DateTime.UtcNow };
        var familyScenario = new Scenario(null, null!, "sim") { Room = familyRoom, Probability = 0.5, LastHit = DateTime.UtcNow };
        var device = new Device("phone:darrell-15-pro", null, TimeSpan.FromSeconds(30)) { Track = true };
        device.Scenarios.Add(laundryScenario);
        device.Scenarios.Add(familyScenario);
        device.BestScenario = laundryScenario;
        state.Devices[device.Id] = device;

        var events = new List<FusionRoomEvent>();
        var history = new Dictionary<string, List<HaStateChange>>();

        void Tick(DateTimeOffset at)
        {
            // Only emit on an actual change, same as production: StateController's websocket forward
            // and the HA discovery publish both fire from CsiFusionService.Tick's "changed" list, not
            // a periodic snapshot -- so a latency metric comparing "occupied-start transition" times
            // means what it says, rather than finding the nearest of many repeated same-state events.
            foreach (var occupancy in fusion.Tick(at.UtcDateTime))
                events.Add(FusionRoomEvent.FromRoomOccupancy(at, occupancy));
        }

        // ---- Baseline warm-up (empty rooms, ~16 minutes of history per node) ----
        var warmupStart = baseTime.AddMinutes(-20);
        for (var i = 0; i < 200; i++)
        {
            var at = warmupStart.AddSeconds(i * 5).UtcDateTime;
            tracker.Observe(laundryNode.Id, 12, at);
            tracker.Observe(familyNode.Id, 11, at);
            tracker.Observe(officeNode.Id, 9, at);
        }

        // ---- Reference case 1+2: Laundry/Family flap (ESPA-463, darrell-15-pro) with CSI leading HA ----
        // Laundry shows sustained motion for the whole flapping episode; Family never does.
        var flapStart = baseTime;
        var flapEnd = flapStart;
        var flapOffsets = new[] { 0, 5, 20, 35, 55, 65, 90, 95, 120, 150, 170, 200, 230, 250, 280, 310, 330, 360 };
        var towardLaundry = true;
        foreach (var offset in flapOffsets)
        {
            var at = flapStart.AddSeconds(offset);
            flapEnd = at;
            tracker.Observe(laundryNode.Id, 95, at.UtcDateTime); // sustained elevated reading
            tracker.Observe(familyNode.Id, 11, at.UtcDateTime);

            laundryScenario.Probability = towardLaundry ? 0.52 : 0.48;
            familyScenario.Probability = towardLaundry ? 0.48 : 0.52;
            laundryScenario.LastHit = DateTime.UtcNow;
            familyScenario.LastHit = DateTime.UtcNow;
            device.BestScenario = towardLaundry ? laundryScenario : familyScenario;

            Tick(at);
            towardLaundry = !towardLaundry;
        }

        // Ground truth: the device is actually in Laundry throughout (that's the whole point of the
        // flap case -- BLE alone cannot tell), and HA's own occupancy sensor only catches the motion
        // ~65s after CSI already had it (documented Laundry 18:35 case).
        // GroundTruthBuilder.AppendTrackerIntervals derives the device id by stripping the
        // "device_tracker." prefix verbatim, so this must match Device.Id exactly (a real HA
        // deployment would use a sanitized entity id and a separate id-mapping step -- out of scope
        // for this synthetic, labels-match-by-construction fixture).
        history[$"device_tracker.{device.Id}"] = new List<HaStateChange>
        {
            new($"device_tracker.{device.Id}", "laundry", flapStart.AddSeconds(-5))
        };
        history["binary_sensor.laundry_occupancy"] = new List<HaStateChange>
        {
            new("binary_sensor.laundry_occupancy", "on", flapStart.AddSeconds(65)),
            new("binary_sensor.laundry_occupancy", "off", flapEnd.AddMinutes(10))
        };
        history["binary_sensor.family_occupancy"] = new List<HaStateChange>
        {
            // Stuck "on" since the morning, per ESPA-463 -- excluded from ground truth by the CLI's
            // default --exclude-entity, not something the fusion engine itself needs to special-case.
            new("binary_sensor.family_occupancy", "on", baseTime.AddHours(-9))
        };

        // ---- Reference case 3: Office false-alarm numbers (sustained-but-brief motion, no device) ----
        var officeBase = flapStart.AddMinutes(20);

        // Episode A: motion while HA's occupancy sensor genuinely agrees -> a real unknown occupant.
        var realAt1 = officeBase;
        var realAt2 = officeBase.AddSeconds(5);
        tracker.Observe(officeNode.Id, 1143, realAt1.UtcDateTime);
        Tick(realAt1);
        tracker.Observe(officeNode.Id, 1230, realAt2.UtcDateTime);
        Tick(realAt2);

        // Let the reading settle back to baseline so the next episode is a fresh motion-started edge.
        for (var i = 1; i <= 6; i++)
            tracker.Observe(officeNode.Id, 9, officeBase.AddSeconds(5 + i * 5).UtcDateTime);
        Tick(officeBase.AddSeconds(40));

        // Episode B: the same sustained-but-brief shape, but HA's occupancy sensor says the room was
        // empty here -> a false alarm (this is what FalseUnknownRate is measuring, not something the
        // fusion engine is expected to drive to zero).
        var falseAt1 = officeBase.AddMinutes(5);
        var falseAt2 = falseAt1.AddSeconds(5);
        tracker.Observe(officeNode.Id, 1143, falseAt1.UtcDateTime);
        Tick(falseAt1);
        tracker.Observe(officeNode.Id, 1230, falseAt2.UtcDateTime);
        Tick(falseAt2);

        history["binary_sensor.office_occupancy"] = new List<HaStateChange>
        {
            new("binary_sensor.office_occupancy", "off", officeBase.AddSeconds(-60)),
            new("binary_sensor.office_occupancy", "on", realAt1.AddSeconds(-2)),
            new("binary_sensor.office_occupancy", "off", realAt2.AddSeconds(10))
        };

        return (history, events);
    }

    private static ConfigRoom Square(string name, double x0, double y0, double x1, double y1) => new()
    {
        Name = name,
        Points = new[] { new[] { x0, y0 }, new[] { x1, y0 }, new[] { x1, y1 }, new[] { x0, y1 } }
    };

    private static Node AddNode(State state, Config config, string id, Point3D location, Floor floor)
    {
        var node = new Node(id, NodeSourceType.Config);
        node.Update(config, new ConfigNode { Id = id, Name = id, Point = new[] { location.X, location.Y, location.Z } }, new[] { floor });
        state.Nodes[id] = node;
        return node;
    }

    private sealed class MockConfigLoaderForFusion : ConfigLoader
    {
        private readonly Config _config;
        public MockConfigLoaderForFusion(Config config) : base(string.Empty)
        {
            _config = config;
            typeof(ConfigLoader).GetProperty(nameof(Config))!.SetValue(this, config);
        }
        public new Config Config => _config;
    }

    private sealed class MockNodeTelemetryStoreForFusion : NodeTelemetryStore
    {
        public MockNodeTelemetryStoreForFusion() : base(null!) { }
        public override bool Online(string id) => true;
        public override NodeTelemetry? Get(string id) => null;
    }

    /// <summary>Minimal no-op IMqttCoordinator -- this console project has no Moq dependency, and
    /// CsiMotionTracker/CsiFusionService only need something to subscribe their events to.</summary>
    private sealed class NullMqttCoordinator : IMqttCoordinator
    {
        public string DiscoveryTopic => "homeassistant";
        public event Func<DeviceSettingsEventArgs, Task>? DeviceConfigReceivedAsync;
        public event Func<DeviceMessageEventArgs, Task>? DeviceMessageReceivedAsync;
        public event Func<MqttApplicationMessageReceivedEventArgs, Task>? MqttMessageReceivedAsync;
        public event Func<NodeSettingReceivedEventArgs, Task>? NodeSettingReceivedAsync;
        public event Func<NodeCsiVarianceReceivedEventArgs, Task>? NodeCsiVarianceReceivedAsync;
        public event Func<NodeTelemetryReceivedEventArgs, Task>? NodeTelemetryReceivedAsync;
        public event Func<NodeTelemetryRemovedEventArgs, Task>? NodeTelemetryRemovedAsync;
        public event Func<NodeStatusReceivedEventArgs, Task>? NodeStatusReceivedAsync;
        public event Func<NodeStatusRemovedEventArgs, Task>? NodeStatusRemovedAsync;
        public event Func<NodeUpdateErrorEventArgs, Task>? NodeUpdateErrorAsync;
        public event EventHandler? MqttMessageMalformed;
        public event EventHandler<PreviousDeviceDiscoveredEventArgs>? PreviousDeviceDiscovered;
        public event Func<DeviceAttributesEventArgs, Task>? DeviceAttributesReceivedAsync;

        public Task EnqueueAsync(string topic, string? payload, bool retain = false) => Task.CompletedTask;
        public Task<bool> TryEnqueueAsync(string topic, string? payload, bool retain = false) => Task.FromResult(true);
        public Task WaitForConnectionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearRetainedAsync(string topicFilter, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
