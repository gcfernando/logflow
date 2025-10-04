namespace LogFlow.Core.Batching.Model;

/*
 * Developer ::> Gehan Fernando 
 * Date      ::> 2025-10-01
 * Contact   ::> f.gehan@gmail.com / + 46 73 701 40 25
*/

/// <summary>
/// Defines configuration options for the <see cref="BatchLogger"/> component.
/// </summary>
/// <remarks>
/// These options control the batching behavior, filtering, sinks, and error handling
/// used by the <see cref="BatchLogger"/>.
/// You can configure batching thresholds, custom sinks, file or database targets,
/// and diagnostic callbacks for error handling and monitoring.
/// </remarks>
public sealed class BatchLoggerOptions
{
    /// <summary>
    /// Gets or sets the total capacity of the internal log channel.
    /// </summary>
    /// <remarks>
    /// When the channel reaches this capacity, the oldest entries are dropped
    /// to make room for new logs.
    /// This prevents unbounded memory growth during bursts of log activity.
    /// </remarks>
    /// <example>
    /// <code language="csharp">
    /// options.Capacity = 50_000;
    /// </code>
    /// </example>
    public int Capacity { get; set; } = 10_000;

    /// <summary>
    /// Gets or sets the maximum number of items to buffer before an immediate flush is triggered.
    /// </summary>
    /// <remarks>
    /// When the number of buffered log entries reaches this threshold, a flush operation
    /// is performed even if the <see cref="FlushInterval"/> has not elapsed yet.
    /// </remarks>
    /// <example>
    /// <code language="csharp">
    /// options.BatchSize = 500;
    /// </code>
    /// </example>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Gets or sets the maximum time between automatic flush operations.
    /// </summary>
    /// <remarks>
    /// A flush is triggered at this cadence when pending log entries exist in the buffer.
    /// If no entries are queued, the flush is skipped until new logs arrive.
    /// </remarks>
    /// <example>
    /// <code language="csharp">
    /// options.FlushInterval = TimeSpan.FromSeconds(1);
    /// </code>
    /// </example>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Gets or sets an optional filter that determines which log entries are enqueued.
    /// </summary>
    /// <remarks>
    /// The delegate signature is:
    /// <c>(LogLevel level, string message, Exception exception, object[] args) =&gt; bool</c>.
    /// Return <see langword="true"/> to allow a log entry, or <see langword="false"/> to ignore it.
    /// The default filter allows all log levels and messages.
    /// </remarks>
    /// <example>
    /// <code language="csharp">
    /// options.Filter = (level, msg, ex, args) => level >= LogLevel.Information;
    /// </code>
    /// </example>
    public Func<Microsoft.Extensions.Logging.LogLevel, string, Exception, object[], bool> Filter { get; set; }
        = static (_, _, _, _) => true;

    /// <summary>
    /// Gets or sets an optional asynchronous delegate that processes batches during a flush operation.
    /// </summary>
    /// <remarks>
    /// When provided, <see cref="BatchLogger"/> invokes this delegate instead of writing directly
    /// to the configured sinks.
    /// This allows custom sink integrations such as Kafka, Elasticsearch, or remote APIs.
    /// If this delegate throws, <see cref="OnInternalError"/> is invoked and the logger
    /// falls back to the default sink pipeline.
    /// </remarks>
    /// <example>
    /// <code language="csharp">
    /// options.OnFlushAsync = async (entries, token) =>
    /// {
    ///     foreach (var e in entries)
    ///         await Console.Out.WriteLineAsync($"{e.Level}: {e.Message}");
    /// };
    /// </code>
    /// </example>
    public Func<IReadOnlyList<BatchLogEntry>, CancellationToken, Task> OnFlushAsync { get; set; }

    /// <summary>
    /// Gets or sets an optional callback that is invoked when internal errors occur.
    /// </summary>
    /// <remarks>
    /// This callback never throws.
    /// It is primarily used for diagnostics when background processing or sink operations fail.
    /// </remarks>
    /// <example>
    /// <code language="csharp">
    /// options.OnInternalError = (context, ex) =>
    ///     Console.Error.WriteLine($"[BatchLogger] {context}: {ex.Message}");
    /// </code>
    /// </example>
    public Action<string, Exception> OnInternalError { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether log entries are forwarded
    /// to the original <see cref="ILogger"/> sink in addition to file or database sinks.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="true"/>.
    /// Set to <see langword="false"/> if you want the batch logger to exclusively
    /// handle output (e.g., file or database only).
    /// </remarks>
    /// <example>
    /// <code language="csharp">
    /// options.ForwardToILoggerSink = false;
    /// </code>
    /// </example>
    public bool ForwardToILoggerSink { get; set; } = true;

    /// <summary>
    /// Gets or sets configuration options for the file sink.
    /// </summary>
    /// <remarks>
    /// Controls how log files are written, rolled, and retained.
    /// Disabled by default; set <see cref="BatchFileOptions.Enabled"/> to <see langword="true"/>
    /// to enable file output.
    /// </remarks>
    public BatchFileOptions File { get; set; } = new();

    /// <summary>
    /// Gets or sets configuration options for the database sink.
    /// </summary>
    /// <remarks>
    /// Supports multiple ADO.NET providers such as SQL Server, PostgreSQL, and MySQL.
    /// Disabled by default; set <see cref="BatchDatabaseOptions.Enabled"/> to <see langword="true"/>
    /// to enable database output.
    /// </remarks>
    public BatchDatabaseOptions Database { get; set; } = new();

    /// <summary>
    /// Gets or sets output formatting options shared by file and database sinks.
    /// </summary>
    /// <remarks>
    /// Controls JSON serialization and text template formatting used
    /// when writing batched entries to sinks.
    /// </remarks>
    public BatchFormatOptions Format { get; set; } = new();
}