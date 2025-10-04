using System.Collections.Concurrent;
using LogFlow.Core.Batching.Model;
using Microsoft.Extensions.Logging;

namespace LogFlow.Core.Batching;

/*
 * Developer ::> Gehan Fernando 
 * Date      ::> 2025-10-01
 * Contact   ::> f.gehan@gmail.com / + 46 73 701 40 25
*/

/// <summary>
/// Provides and manages <see cref="BatchLogger"/> instances for ASP.NET Core logging.
/// </summary>
/// <remarks>
/// This class acts as a bridge between the standard <see cref="ILoggerFactory"/>
/// and the <see cref="BatchLogger"/> batching implementation.
/// It ensures that each logging category receives a unique, thread-safe instance
/// of <see cref="BatchLogger"/>, while sharing common batching configuration and sinks.
/// </remarks>
/// <example>
/// Example of adding <see cref="BatchLoggerProvider"/> manually:
/// <code language="csharp">
/// var factory = LoggerFactory.Create(builder =>
/// {
///     builder.AddConsole();
/// });
///
/// var options = new BatchLoggerOptions
/// {
///     Capacity = 20_000,
///     BatchSize = 200,
///     FlushInterval = TimeSpan.FromMilliseconds(500)
/// };
///
/// using var provider = new BatchLoggerProvider(factory, options);
/// var logger = provider.CreateLogger("App");
///
/// logger.LogInformation("This message is batched asynchronously.");
/// </code>
/// </example>
public sealed class BatchLoggerProvider : ILoggerProvider
{
    private readonly ILoggerFactory _inner;
    private readonly BatchLoggerOptions _options;
    private readonly ConcurrentDictionary<string, BatchLogger> _loggers = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="BatchLoggerProvider"/> class.
    /// </summary>
    /// <param name="inner">
    /// The inner <see cref="ILoggerFactory"/> that provides base <see cref="ILogger"/> instances.
    /// These instances are wrapped by <see cref="BatchLogger"/> to enable batching.
    /// </param>
    /// <param name="options">
    /// The batching configuration options shared across all created loggers.
    /// </param>
    /// <remarks>
    /// The provider does not own the inner <see cref="ILoggerFactory"/>; it simply decorates it.
    /// </remarks>
    public BatchLoggerProvider(ILoggerFactory inner, BatchLoggerOptions options)
    {
        _inner = inner;
        _options = options;
    }

    /// <summary>
    /// Creates or retrieves a cached <see cref="BatchLogger"/> for the specified category name.
    /// </summary>
    /// <param name="categoryName">The category name for messages produced by the logger.</param>
    /// <returns>
    /// A <see cref="BatchLogger"/> instance that buffers and flushes logs asynchronously.
    /// </returns>
    /// <remarks>
    /// The provider maintains one <see cref="BatchLogger"/> instance per unique category.
    /// Instances are thread-safe and reused across the application lifetime.
    /// </remarks>
    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName,
            name => new BatchLogger(_inner.CreateLogger(name), _options));

    /// <summary>
    /// Releases all resources used by the provider and its managed <see cref="BatchLogger"/> instances.
    /// </summary>
    /// <remarks>
    /// Disposes each <see cref="BatchLogger"/> to ensure that any buffered log entries
    /// are flushed to their configured sinks before shutdown.
    /// </remarks>
    public void Dispose()
    {
        foreach (var logger in _loggers.Values)
        {
            logger.Dispose();
        }

        _loggers.Clear();
    }
}