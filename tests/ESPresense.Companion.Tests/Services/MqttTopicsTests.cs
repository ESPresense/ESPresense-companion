using ESPresense.Services;

namespace ESPresense.Companion.Tests.Services;

/// <summary>
/// Pins every MQTT topic string, byte for byte. These are wire format shared with nodes, Home Assistant and
/// other companions; a failure here means a breaking protocol change, not a test to update casually.
/// </summary>
public class MqttTopicsTests
{
    [Test]
    public void Constants_AreUnchanged()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MqttTopics.CompanionStatus, Is.EqualTo("espresense/companion/status"));
            Assert.That(MqttTopics.Online, Is.EqualTo("online"));
            Assert.That(MqttTopics.Offline, Is.EqualTo("offline"));
            Assert.That(MqttTopics.CompanionLeasePrefix, Is.EqualTo("espresense/companion/lease/"));
            Assert.That(MqttTopics.CompanionTelemetry, Is.EqualTo("espresense/companion/telemetry"));
        });
    }

    [Test]
    public void Builders_ProduceTheWireTopics()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MqttTopics.NodeTelemetry("node-1"), Is.EqualTo("espresense/rooms/node-1/telemetry"));
            Assert.That(MqttTopics.NodeStatus("node-1"), Is.EqualTo("espresense/rooms/node-1/status"));
            Assert.That(MqttTopics.NodeUpdateError("node-1"), Is.EqualTo("espresense/rooms/node-1/update_error"));
            Assert.That(MqttTopics.NodeSetting("node-1", "absorption"), Is.EqualTo("espresense/rooms/node-1/absorption"));
            Assert.That(MqttTopics.NodeSettingSet("node-1", "absorption"), Is.EqualTo("espresense/rooms/node-1/absorption/set"));
            Assert.That(MqttTopics.NodeSettingSet("*", "absorption"), Is.EqualTo("espresense/rooms/*/absorption/set"));
            Assert.That(MqttTopics.NodeAllTopicsFilter("node-1"), Is.EqualTo("espresense/rooms/node-1/#"));
            Assert.That(MqttTopics.DeviceMessage("dev-1", "node-1"), Is.EqualTo("espresense/devices/dev-1/node-1"));
            Assert.That(MqttTopics.DeviceSettings("dev-1"), Is.EqualTo("espresense/settings/dev-1/config"));
            Assert.That(MqttTopics.DeviceAttributes("dev-1"), Is.EqualTo("espresense/companion/dev-1/attributes"));
            Assert.That(MqttTopics.CompanionDeviceState("dev-1"), Is.EqualTo("espresense/companion/dev-1"));
            Assert.That(MqttTopics.CompanionLease("optimizer"), Is.EqualTo("espresense/companion/lease/optimizer"));
            Assert.That(MqttTopics.DeviceTrackerDiscoveryFilter("homeassistant"), Is.EqualTo("homeassistant/device_tracker/+/config"));
            Assert.That(MqttTopics.DiscoveryConfig("ha", "device_tracker", "espresense-dev-1"), Is.EqualTo("ha/device_tracker/espresense-dev-1/config"));
        });
    }

    [Test]
    public void Subscriptions_AreUnchangedAndInOrder()
    {
        Assert.That(MqttTopics.Subscriptions("homeassistant"), Is.EqualTo(new[]
        {
            "espresense/devices/+/+",
            "espresense/settings/+/config",
            "espresense/rooms/+/+",
            "espresense/rooms/*/+/set",
            "homeassistant/device_tracker/+/config",
            "espresense/companion/+/attributes",
            "espresense/companion/lease/+",
        }));
    }

    [Test]
    public void Subscriptions_UseTheConfiguredDiscoveryPrefix()
    {
        Assert.That(MqttTopics.Subscriptions("custom-ha"), Does.Contain("custom-ha/device_tracker/+/config"));
    }
}
