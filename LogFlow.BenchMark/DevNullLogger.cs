using Microsoft.Extensions.Logging;

namespace LogFlow.BenchMark;

internal sealed class DevNullLogger : ILogger
{
    public static readonly DevNullLogger Instance = new();

    public IDisposable BeginScope<TState>(TState state) => NoOpScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
    { }

    private sealed class NoOpScope : IDisposable
    {
        public static readonly NoOpScope Instance = new();

        public void Dispose()
        { }
    }
}