using System.Text.Json;
using System.Linq;
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
    private List<LeaseService> _services = null!;

    [SetUp]
    public void Setup()
    {
        _mockMqtt = new Mock<IMqttCoordinator>();
        _mockLogger = new Mock<ILogger<LeaseService>>();
        _mqttMessages = new Dictionary<string, string?>();
        _services = new List<LeaseService>();

        // Setup MQTT mock to store published messages
        _mockMqtt
            .Setup(m => m.WaitForConnectionAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockMqtt
            .Setup(m => m.EnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .Callback<string, string?, bool>((topic, payload, retain) =>
            {
                _mqttMessages[topic] = payload;
                foreach (var svc in _services.ToList())
                {
                    SimulateMqttMessage(svc, topic, payload).GetAwaiter().GetResult();
                }
            })
            .Returns(Task.CompletedTask);
    }

    private LeaseService CreateService()
    {
        var service = new LeaseService(_mockMqtt.Object, _mockLogger.Object);
        _services.Add(service);
        return service;
    }

    // Helper to simulate an empty/no-holder state for a lease (like MQTT retained message being empty)
    private async Task SimulateNoLeaseHolder(LeaseService service, string leaseName)
    {
        // Simulate receiving an expired lease message (empty payload means no holder)
        var expiredLease = new LeaseInfo
        {
            InstanceId = "nobody",
            ExpiresAt = DateTime.UtcNow.AddSeconds(-1) // Already expired
        };
        await SimulateMqttMessage(service, $"espresense/companion/lease/{leaseName}",
            JsonSerializer.Serialize(expiredLease));
    }

    [Test]
    public async Task AcquireAsync_FirstInstance_AcquiresLeaseSuccessfully()
    {
        // Arrange
        var service = CreateService();
        await SimulateNoLeaseHolder(service, "test-lease");

        // Act - Use short timeout to avoid 30s wait for "nobody" lease
        await using var lease = await service.AcquireAsync("test-lease", timeout: TimeSpan.FromMilliseconds(100));

        // Assert
        Assert.That(lease, Is.Not.Null);
        Assert.That(service.HasLease("test-lease"), Is.True);

        // Verify MQTT message was published
        _mockMqtt.Verify(
            m => m.EnqueueAsync(
                "espresense/companion/lease/test-lease",
                It.IsAny<string>(),
                true),
            Times.AtLeastOnce);

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
        var service1 = CreateService();
        var service2 = CreateService();
        await SimulateNoLeaseHolder(service1, "test-lease");

        // First service acquires lease
        await using var lease1 = await service1.AcquireAsync("test-lease", timeout: TimeSpan.FromMilliseconds(100));
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
        var service1 = CreateService();
        var service2 = CreateService();

        // Create an expired lease
        var expiredLease = new LeaseInfo
        {
            InstanceId = "expired-instance",
            ExpiresAt = DateTime.UtcNow.AddSeconds(-10) // Expired 10 seconds ago
        };

        var expiredPayload = JsonSerializer.Serialize(expiredLease);
        await SimulateMqttMessage(service2, "espresense/companion/lease/test-lease", expiredPayload);

        // Act - Second service tries to acquire
        await using var lease2 = await service2.AcquireAsync("test-lease", timeout: TimeSpan.FromMilliseconds(100));

        // Assert
        Assert.That(lease2, Is.Not.Null);
        Assert.That(service2.HasLease("test-lease"), Is.True);
    }

    [Test]
    public async Task AcquireAsync_LosesRaceWhenAnotherInstanceObservedAfterPublish()
    {
        // Arrange
        var service = CreateService();
        await SimulateNoLeaseHolder(service, "race-lease");
        _mockMqtt
            .Setup(m => m.EnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .Callback<string, string?, bool>((topic, payload, retain) =>
            {
                _mqttMessages[topic] = payload;

                var otherLease = new LeaseInfo
                {
                    InstanceId = "other-instance",
                    ExpiresAt = DateTime.UtcNow.AddMinutes(1)
                };
                var otherPayload = JsonSerializer.Serialize(otherLease);

                // Simulate broker delivering the other instance's lease before we confirm ours.
                SimulateMqttMessage(service, topic, otherPayload).GetAwaiter().GetResult();
                SimulateMqttMessage(service, topic, payload).GetAwaiter().GetResult();
            })
            .Returns(Task.CompletedTask);

        // Act
        var lease = await service.AcquireAsync("race-lease", timeout: TimeSpan.FromSeconds(1));

        // Assert
        Assert.That(lease, Is.Null);
        Assert.That(service.HasLease("race-lease"), Is.False);
    }

    [Test]
    public async Task ObservedExpiredLease_RemovesLocalLease()
    {
        // Arrange
        var service = CreateService();
        await SimulateNoLeaseHolder(service, "test-lease");
        await using var lease = await service.AcquireAsync("test-lease", timeout: TimeSpan.FromMilliseconds(100));
        Assert.That(lease, Is.Not.Null);
        Assert.That(service.HasLease("test-lease"), Is.True);

        var publishedPayload = _mqttMessages["espresense/companion/lease/test-lease"];
        var currentLease = JsonSerializer.Deserialize<LeaseInfo>(publishedPayload!);
        var expiredPayload = JsonSerializer.Serialize(new LeaseInfo
        {
            InstanceId = currentLease!.InstanceId,
            ExpiresAt = DateTime.UtcNow.AddSeconds(-1)
        });

        // Act - observe our own expired lease message
        await SimulateMqttMessage(service, "espresense/companion/lease/test-lease", expiredPayload);

        // Give Task.Run time to complete
        await Task.Delay(50);

        // Assert
        Assert.That(service.HasLease("test-lease"), Is.False);
    }

    [Test]
    public async Task ObservedOtherInstanceLease_RemovesLocalLease()
    {
        // Arrange
        var service = CreateService();
        await SimulateNoLeaseHolder(service, "test-lease");
        await using var lease = await service.AcquireAsync("test-lease", timeout: TimeSpan.FromMilliseconds(100));
        Assert.That(lease, Is.Not.Null);
        Assert.That(service.HasLease("test-lease"), Is.True);

        var takeoverPayload = JsonSerializer.Serialize(new LeaseInfo
        {
            InstanceId = "other-instance",
            ExpiresAt = DateTime.UtcNow.AddMinutes(1)
        });

        // Act - observe another instance taking the lease
        await SimulateMqttMessage(service, "espresense/companion/lease/test-lease", takeoverPayload);

        // Give Task.Run time to complete
        await Task.Delay(50);

        // Assert
        Assert.That(service.HasLease("test-lease"), Is.False);
    }

    [Test]
    public async Task ReleaseAsync_ReleasesLeaseAndClearsRetainedMessage()
    {
        // Arrange
        var service = CreateService();
        await SimulateNoLeaseHolder(service, "test-lease");
        await using var lease = await service.AcquireAsync("test-lease", timeout: TimeSpan.FromMilliseconds(100));
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
        var service = CreateService();
        await SimulateNoLeaseHolder(service, "test-lease");
        LeaseHandle? lease;

        // Act
        await using (lease = await service.AcquireAsync("test-lease", timeout: TimeSpan.FromMilliseconds(100)))
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
        var service = CreateService();

        // Act & Assert
        Assert.That(service.HasLease("non-existent"), Is.False);
    }

    [Test]
    public async Task AcquireAsync_SameInstanceTwice_RenewsExistingLease()
    {
        // Arrange
        var service = CreateService();
        await SimulateNoLeaseHolder(service, "test-lease");

        // Act - Acquire twice
        await using var lease1 = await service.AcquireAsync("test-lease", timeout: TimeSpan.FromMilliseconds(100));
        await using var lease2 = await service.AcquireAsync("test-lease", timeout: TimeSpan.FromMilliseconds(100));

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
        var service = CreateService();
        await SimulateNoLeaseHolder(service, "lease1");
        await SimulateNoLeaseHolder(service, "lease2");
        await SimulateNoLeaseHolder(service, "lease3");
        await using var lease1 = await service.AcquireAsync("lease1", timeout: TimeSpan.FromMilliseconds(100));
        await using var lease2 = await service.AcquireAsync("lease2", timeout: TimeSpan.FromMilliseconds(100));
        await using var lease3 = await service.AcquireAsync("lease3", timeout: TimeSpan.FromMilliseconds(100));

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
        var service = CreateService();
        await SimulateNoLeaseHolder(service, "lease1");
        await SimulateNoLeaseHolder(service, "lease2");
        await SimulateNoLeaseHolder(service, "lease3");

        // Act - Acquire multiple different leases
        await using var lease1 = await service.AcquireAsync("lease1", timeout: TimeSpan.FromMilliseconds(100));
        await using var lease2 = await service.AcquireAsync("lease2", timeout: TimeSpan.FromMilliseconds(100));
        await using var lease3 = await service.AcquireAsync("lease3", timeout: TimeSpan.FromMilliseconds(100));

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
        var service = CreateService();

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
        var service1 = CreateService();
        var service2 = CreateService();
        await SimulateNoLeaseHolder(service1, "test-lease");

        // First service acquires lease
        await using var lease1 = await service1.AcquireAsync("test-lease", timeout: TimeSpan.FromMilliseconds(100));

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
                ct: cts.Token));
    }

    // Helper method to simulate MQTT message reception
    private async Task SimulateMqttMessage(LeaseService service, string topic, string? payload)
    {
        // Use reflection to invoke the private OnMqttMessage handler
        var method = typeof(LeaseService).GetMethod(
            "OnMqttMessage",
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
