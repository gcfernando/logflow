using Microsoft.Extensions.Logging;

namespace LogFlow.Core.Batching.Model;

/*
 * Developer ::> Gehan Fernando
 * Date      ::> 2025-10-01
 * Contact   ::> f.gehan@gmail.com / + 46 73 701 40 25
*/
public readonly record struct BatchLogEntry(
    LogLevel Level,
    string Message,
    Exception Exception,
    IReadOnlyList<object> Args
);