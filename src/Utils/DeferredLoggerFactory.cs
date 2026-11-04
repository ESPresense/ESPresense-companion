using Microsoft.Extensions.Logging.Abstractions;

namespace ESPresense.Utils;

/// <summary>
/// Transitional (#1846): supplies an <see cref="ILogger"/> to types that are still constructed without one by
/// <c>State</c>, <c>DeviceTracker</c>, <c>MqttCoordinator</c> or <c>MultiScenarioLocator</c>, whose call sites
/// can't change until #1843, #1844 and #1845 merge. Those types take an optional logger and fall back to this.
/// Program.cs points it at the host's <see cref="ILoggerFactory"/>, so entries still get the type's category
/// and Serilog's per-namespace levels apply; until then (and in tests) the loggers discard everything.
/// LoggingConventionTests limits which files may use it.
/// TODO(#1843, #1844, #1845): pass loggers from those call sites and delete this class.
/// </summary>
public static class DeferredLoggerFactory
{
    private static ILoggerFactory _factory = NullLoggerFactory.Instance;

    public static void Use(ILoggerFactory factory) => _factory = factory;

    /// <summary>A logger for <paramref name="categoryType"/> that resolves against the current factory on use.</summary>
    public static ILogger CreateLogger(Type categoryType) => new Forwarding(categoryType);

    /// <inheritdoc cref="CreateLogger(Type)"/>
    public static ILogger<T> CreateLogger<T>() => new Forwarding<T>();

    private class Forwarding(Type category) : ILogger
    {
        private ILoggerFactory? _from;
        private ILogger _inner = NullLogger.Instance;

        private ILogger Inner
        {
            get
            {
                var factory = _factory;
                if (!ReferenceEquals(factory, _from))
                {
                    _inner = factory.CreateLogger(category);
                    _from = factory;
                }
                return _inner;
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => Inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => Inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Inner.Log(logLevel, eventId, state, exception, formatter);
    }

    private sealed class Forwarding<T>() : Forwarding(typeof(T)), ILogger<T>;
}
