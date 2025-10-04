using System.Runtime.CompilerServices;
using LogFlow.Core.Batching.Model;
using Microsoft.Extensions.Logging;

namespace LogFlow.Core.Batching;

/// <summary>
/// Provides extension methods for accessing <see cref="BatchLogger"/> functionality
/// and metrics from a standard <see cref="ILogger"/> instance.
/// </summary>
/// <remarks>
/// This helper allows developers to retrieve the underlying <see cref="BatchLogger"/> instance
/// (if available) from any <see cref="ILogger"/>.
/// It is primarily used to access batching metrics or trigger custom flush operations.
/// </remarks>
/// <example>
/// <para><b>Example: Accessing batch metrics</b></para>
/// <code language="csharp">
/// var logger = loggerFactory.CreateLogger("App");
/// var batchLogger = logger.AsBatch();
///
/// // Read metrics
/// Console.WriteLine($"Dropped: {batchLogger.Metrics.DroppedCount}");
/// Console.WriteLine($"Flushed: {batchLogger.Metrics.TotalFlushed}");
/// </code>
///
/// <para><b>Example: Manual flush</b></para>
/// <code language="csharp">
/// await logger.AsBatch().FlushAsync();
/// </code>
/// </example>
public static class BatchLoggerAccessExtensions
{
    private static readonly ConditionalWeakTable<ILogger, BatchLogger> _cache = [];

    /// <summary>
    /// Returns the underlying <see cref="BatchLogger"/> associated with this <see cref="ILogger"/>.
    /// </summary>
    /// <param name="logger">The source logger instance.</param>
    /// <returns>
    /// The associated <see cref="BatchLogger"/> instance.
    /// If the provided logger is not already a <see cref="BatchLogger"/>,
    /// a new one is created and cached.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="logger"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// Use this method to access batching-specific functionality such as:
    /// <list type="bullet">
    /// <item><description>Inspecting <see cref="BatchLogger.Metrics"/> for telemetry.</description></item>
    /// <item><description>Forcing a manual flush using <c>FlushAsync()</c>.</description></item>
    /// <item><description>Hooking into custom sinks or events.</description></item>
    /// </list>
    /// <para>
    /// If the input <see cref="ILogger"/> is not already a <see cref="BatchLogger"/>,
    /// this method wraps it in a new <see cref="BatchLogger"/> instance using default options.
    /// Subsequent calls with the same logger return the same cached wrapper.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code language="csharp">
    /// var batch = logger.AsBatch();
    /// Console.WriteLine(batch.Metrics.BatchCount);
    /// </code>
    /// </example>
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