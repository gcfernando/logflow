using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LogFlow.Core.Batching;

/// <summary>
/// Provides <see cref="BatchLogger"/> instances for ASP.NET Core logging.
/// Wraps the underlying logger factory to support batched logging.
/// </summary>
public sealed class BatchLoggerProvider : ILoggerProvider
{
    private readonly ILoggerFactory _inner;
    private readonly BatchLoggerOptions _options;
    private readonly ConcurrentDictionary<string, BatchLogger> _loggers = new();

    public BatchLoggerProvider(ILoggerFactory inner, BatchLoggerOptions options)
    {
        _inner = inner;
        _options = options;
    }

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, name => new BatchLogger(_inner.CreateLogger(name), _options));

    public void Dispose()
    {
        foreach (var logger in _loggers.Values)
        {
            logger.Dispose();
        }

        _loggers.Clear();
    }
}