using System.Collections.Concurrent;
using LogFlow.Core.Batching.Model;
using Microsoft.Extensions.Logging;

namespace LogFlow.Core.Batching;

/*
 * Developer ::> Gehan Fernando 
 * Date      ::> 2025-10-01
 * Contact   ::> f.gehan@gmail.com / + 46 73 701 40 25
*/

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
        => _loggers.GetOrAdd(categoryName,
            name => new BatchLogger(_inner.CreateLogger(name), _options));

    public void Dispose()
    {
        foreach (var logger in _loggers.Values)
        {
            logger.Dispose();
        }

        _loggers.Clear();
    }
}