using ESPresense.Models;
using ESPresense.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace ESPresense.Companion.Tests.Models;

public class StateLoggingTests
{
    [Test]
    public void DependencyInjection_GivesStateTheHostLoggerFactory()
    {
        // The "no locators enabled" fallback warning is logged through the factory State gets from DI.
        var factory = new Mock<ILoggerFactory>();
        factory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(Mock.Of<ILogger>());

        var services = new ServiceCollection();
        services.AddSingleton(new ConfigLoader(Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString())));
        services.AddSingleton(new NodeTelemetryStore(Mock.Of<IMqttCoordinator>()));
        services.AddSingleton(factory.Object);
        services.AddSingleton<State>();

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<State>();

        factory.Verify(f => f.CreateLogger(typeof(ScenarioFactory).FullName!), Times.Once);
    }
}
