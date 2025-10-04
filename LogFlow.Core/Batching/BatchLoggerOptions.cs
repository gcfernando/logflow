namespace LogFlow.Core.Batching;

public sealed class BatchLoggerOptions
{
    /// <summary>
    /// Total in-flight capacity of the channel. When full, it drops oldest entries.
    /// </summary>
    public int Capacity { get; set; } = 10_000;

    /// <summary>
    /// Max items to buffer before an immediate flush is triggered.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Time-based flush interval (flush occurs at this cadence when there are pending items).
    /// </summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Optional filter to prevent enqueueing certain log entries (returns true to allow).
    /// Signature: (level, message, exception, args) => bool
    /// Default: allow all.
    /// </summary>
    public Func<Microsoft.Extensions.Logging.LogLevel, string, Exception, object[], bool> Filter { get; set; }
        = static (_, _, _, _) => true;

    /// <summary>
    /// Optional batch sink hook. If provided, BatchLogger will call this delegate on flush
    /// instead of writing directly to the underlying ILogger sink. If the delegate throws,
    /// BatchLogger will report via OnInternalError and fall back to the original sink.
    /// </summary>
    public Func<IReadOnlyList<BatchLogEntry>, CancellationToken, Task> OnFlushAsync { get; set; }

    /// <summary>
    /// Optional callback for internal errors in the background worker or during flush.
    /// Never throws. Useful for diagnostics if the underlying sink is failing.
    /// </summary>
    public Action<string, Exception> OnInternalError { get; set; }
}