using System.Runtime.CompilerServices;
using LogFlow.Core.Batching.Model;
using Microsoft.Extensions.Logging;

namespace LogFlow.Core.Batching;

/*
 * Developer ::> Gehan Fernando 
 * Date      ::> 2025-10-01
 * Contact   ::> f.gehan@gmail.com / + 46 73 701 40 25
*/

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