using System.Text.Json;
using ESPresense.Services;
using MQTTnet;
using Moq;
using Microsoft.Extensions.Logging;

namespace ESPresense.Companion.Tests;

public class LeaseServiceTests
{
    private Mock<IMqttCoordinator> _mockMqtt = null!;
    private Mock<ILogger<LeaseService>> _mockLogger = null!;
    private Dictionary<string, string?> _mqttMessages = null!;

    [SetUp]
    public void Setup()
    {
        _mockMqtt = new Mock<IMqttCoordinator>();
        _mockLogger = new Mock<ILogger<LeaseService>>();
        _mqttMessages = new Dictionary<string, string?>();

        // Setup MQTT mock to store published messages
        _mockMqtt
            .Setup(m => m.WaitForConnectionAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockMqtt
            .Setup(m => m.EnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .Callback<string, string?, bool>((topic, payload, retain) =>
            {
                _mqttMessages[topic] = payload;
            })
            .Returns(Task.CompletedTask);
    }

    [Test]
    public async Task AcquireAsync_FirstInstance_AcquiresLeaseSuccessfully()
    {
        // Arrange
        var service = new LeaseService(_mockMqtt.Object, _mockLogger.Object);

        // Act
        await using var lease = await service.AcquireAsync("test-lease", leaseDurationSecs: 60, renewalIntervalSecs: 10);

        // Assert
        Assert.That(lease, Is.Not.Null);
        Assert.That(service.HasLease("test-lease"), Is.True);

        // Verify MQTT message was published
        _mockMqtt.Verify(
            m => m.EnqueueAsync(
                "espresense/companion/lease/test-lease",
                It.IsAny<string>(),
                true),
            Times.Once);

        // Verify the lease info in the published message
        var publishedPayload = _mqttMessages["espresense/companion/lease/test-lease"];
        Assert.That(publishedPayload, Is.Not.Null);

        var leaseInfo = JsonSerializer.Deserialize<LeaseInfo>(publishedPayload!);
        Assert.That(leaseInfo, Is.Not.Null);
        Assert.That(leaseInfo!.InstanceId, Is.Not.Empty);
        Assert.That(leaseInfo.ExpiresAt, Is.GreaterThan(DateTime.UtcNow));
    }

    [Test]
    public async Task AcquireAsync_WithTimeout_ReturnsNullWhenLeaseHeld()
    {
        // Arrange
        var service1 = new LeaseService(_mockMqtt.Object, _mockLogger.Object);
        var service2 = new LeaseService(_mockMqtt.Object, _mockLogger.Object);

        // First service acquires lease
        await using var lease1 = await service1.AcquireAsync("test-lease", leaseDurationSecs: 120);
        Assert.That(lease1, Is.Not.Null);

        // Simulate the first service's lease being observed by the second service
        var publishedPayload = _mqttMessages["espresense/companion/lease/test-lease"];
        await SimulateMqttMessage(service2, "espresense/companion/lease/test-lease", publishedPayload);

        // Act - Second service tries to acquire with short timeout
        var lease2 = await service2.AcquireAsync("test-lease", timeout: TimeSpan.FromSeconds(1));

        // Assert
        Assert.That(lease2, Is.Null);
        Assert.That(service2.HasLease("test-lease"), Is.False);
    }

    [Test]
    public async Task AcquireAsync_ExpiredLease_TakesOverSuccessfully()
    {
        // Arrange
        var service1 = new LeaseService(_mockMqtt.Object, _mockLogger.Object);
        var service2 = new LeaseService(_mockMqtt.Object, _mockLogger.Object);

        // Create an expired lease
        var expiredLease = new LeaseInfo
        {
            InstanceId = "expired-instance",
            ExpiresAt = DateTime.UtcNow.AddSeconds(-10) // Expired 10 seconds ago
        };

        var expiredPayload = JsonSerializer.Serialize(expiredLease);
        await SimulateMqttMessage(service2, "espresense/companion/lease/test-lease", expiredPayload);

        // Act - Second service tries to acquire
        await using var lease2 = await service2.AcquireAsync("test-lease", leaseDurationSecs: 60);

        // Assert
        Assert.That(lease2, Is.Not.Null);
        Assert.That(service2.HasLease("test-lease"), Is.True);
    }

    [Test]
    public async Task ReleaseAsync_ReleasesLeaseAndClearsRetainedMessage()
    {
        // Arrange
        var service = new LeaseService(_mockMqtt.Object, _mockLogger.Object);
        await using var lease = await service.AcquireAsync("test-lease");
        Assert.That(lease, Is.Not.Null);
        Assert.That(service.HasLease("test-lease"), Is.True);

        // Act
        await service.ReleaseAsync("test-lease");

        // Assert
        Assert.That(service.HasLease("test-lease"), Is.False);

        // Verify null payload was published to clear retained message
        _mockMqtt.Verify(
            m => m.EnqueueAsync(
                "espresense/companion/lease/test-lease",
                null,
                true),
            Times.Once);
    }

    [Test]
    public async Task Dispose_AutomaticallyReleasesLease()
    {
        // Arrange
        var service = new LeaseService(_mockMqtt.Object, _mockLogger.Object);
        LeaseHandle? lease;

        // Act
        await using (lease = await service.AcquireAsync("test-lease"))
        {
            Assert.That(lease, Is.Not.Null);
            Assert.That(service.HasLease("test-lease"), Is.True);
        } // Dispose happens here

        // Assert
        Assert.That(service.HasLease("test-lease"), Is.False);

        // Verify null payload was published
        _mockMqtt.Verify(
            m => m.EnqueueAsync(
                "espresense/companion/lease/test-lease",
                null,
                true),
            Times.Once);
    }

    [Test]
    public async Task HasLease_ReturnsFalseForNonExistentLease()
    {
        // Arrange
        var service = new LeaseService(_mockMqtt.Object, _mockLogger.Object);

        // Act & Assert
        Assert.That(service.HasLease("non-existent"), Is.False);
    }

    [Test]
    public async Task AcquireAsync_SameInstanceTwice_RenewsExistingLease()
    {
        // Arrange
        var service = new LeaseService(_mockMqtt.Object, _mockLogger.Object);

        // Act - Acquire twice
        await using var lease1 = await service.AcquireAsync("test-lease", leaseDurationSecs: 60);
        await using var lease2 = await service.AcquireAsync("test-lease", leaseDurationSecs: 60);

        // Assert
        Assert.That(lease1, Is.Not.Null);
        Assert.That(lease2, Is.Not.Null);
        Assert.That(service.HasLease("test-lease"), Is.True);

        // Should have published at least twice (initial acquire + renewal)
        _mockMqtt.Verify(
            m => m.EnqueueAsync(
                "espresense/companion/lease/test-lease",
                It.IsAny<string>(),
                true),
            Times.AtLeast(2));
    }

    [Test]
    public async Task ReleaseAllAsync_ReleasesAllLeases()
    {
        // Arrange
        var service = new LeaseService(_mockMqtt.Object, _mockLogger.Object);
        await using var lease1 = await service.AcquireAsync("lease1");
        await using var lease2 = await service.AcquireAsync("lease2");
        await using var lease3 = await service.AcquireAsync("lease3");

        Assert.That(service.HasLease("lease1"), Is.True);
        Assert.That(service.HasLease("lease2"), Is.True);
        Assert.That(service.HasLease("lease3"), Is.True);

        // Act
        await service.ReleaseAllAsync();

        // Assert
        Assert.That(service.HasLease("lease1"), Is.False);
        Assert.That(service.HasLease("lease2"), Is.False);
        Assert.That(service.HasLease("lease3"), Is.False);
    }

    [Test]
    public async Task AcquireAsync_MultipleLeasesSimultaneously_AllSucceed()
    {
        // Arrange
        var service = new LeaseService(_mockMqtt.Object, _mockLogger.Object);

        // Act - Acquire multiple different leases
        await using var lease1 = await service.AcquireAsync("lease1");
        await using var lease2 = await service.AcquireAsync("lease2");
        await using var lease3 = await service.AcquireAsync("lease3");

        // Assert
        Assert.That(lease1, Is.Not.Null);
        Assert.That(lease2, Is.Not.Null);
        Assert.That(lease3, Is.Not.Null);
        Assert.That(service.HasLease("lease1"), Is.True);
        Assert.That(service.HasLease("lease2"), Is.True);
        Assert.That(service.HasLease("lease3"), Is.True);
    }

    [Test]
    public void AcquireAsync_EmptyLeaseName_ThrowsArgumentException()
    {
        // Arrange
        var service = new LeaseService(_mockMqtt.Object, _mockLogger.Object);

        // Act & Assert
        Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.AcquireAsync(""));

        Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.AcquireAsync(null!));

        Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.AcquireAsync("   "));
    }

    [Test]
    public async Task AcquireAsync_Cancellation_StopsTrying()
    {
        // Arrange
        var service1 = new LeaseService(_mockMqtt.Object, _mockLogger.Object);
        var service2 = new LeaseService(_mockMqtt.Object, _mockLogger.Object);

        // First service acquires lease
        await using var lease1 = await service1.AcquireAsync("test-lease", leaseDurationSecs: 120);

        // Simulate the first service's lease being observed by the second service
        var publishedPayload = _mqttMessages["espresense/companion/lease/test-lease"];
        await SimulateMqttMessage(service2, "espresense/companion/lease/test-lease", publishedPayload);

        // Create cancellation token that cancels after short delay
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // Act & Assert
        Assert.ThrowsAsync<TaskCanceledException>(async () =>
            await service2.AcquireAsync(
                "test-lease",
                timeout: null, // Would wait forever without cancellation
                cancellationToken: cts.Token));
    }

    // Helper method to simulate MQTT message reception
    private async Task SimulateMqttMessage(LeaseService service, string topic, string? payload)
    {
        // Use reflection to invoke the private OnMqttMessageReceived handler
        var method = typeof(LeaseService).GetMethod(
            "OnMqttMessageReceived",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        if (method != null)
        {
            var mqttMessage = new MqttApplicationMessage
            {
                Topic = topic,
                PayloadSegment = payload != null
                    ? new ArraySegment<byte>(System.Text.Encoding.UTF8.GetBytes(payload))
                    : new ArraySegment<byte>()
            };

            var eventArgs = new MqttApplicationMessageReceivedEventArgs(
                "test-client",
                mqttMessage,
                new MQTTnet.Packets.MqttPublishPacket(),
                null!);

            var task = method.Invoke(service, new[] { eventArgs }) as Task;
            if (task != null)
            {
                await task;
            }
        }
    }
}
