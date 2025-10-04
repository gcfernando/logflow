using Microsoft.Extensions.Logging;

namespace LogFlow.BenchMark;

internal sealed class DevNullLoggerFactory : ILoggerFactory, ILoggerProvider
{
    public void AddProvider(ILoggerProvider provider)
    { }

    public ILogger CreateLogger(string categoryName) => DevNullLogger.Instance;

    public void Dispose()
    { }
}