using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace LogFlow.Core.Batching;

public static class BatchLoggerAccessExtensions
{
    private static readonly ConditionalWeakTable<ILogger, BatchLogger> _cache = [];

    public static BatchLogger AsBatch(this ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        return logger is BatchLogger bl
            ? bl
            : _cache.GetValue(logger, l =>
            {
                var opts = new BatchLoggerOptions();
                return new BatchLogger(l, opts);
            });
    }
}