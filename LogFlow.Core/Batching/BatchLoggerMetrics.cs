using System.Runtime.CompilerServices;

namespace LogFlow.Core.Batching;

/// <summary>
/// Provides thread-safe runtime metrics for the <see cref="BatchLogger"/> component.
/// </summary>
/// <remarks>
/// These metrics expose internal performance counters and operational statistics
/// useful for telemetry systems such as Prometheus, OpenTelemetry, or custom dashboards.
/// All values are maintained atomically and can be safely read from multiple threads.
/// </remarks>
/// <example>
/// Example of reading metrics:
/// <code language="csharp">
/// var batchLogger = logger.AsBatch();
/// Console.WriteLine($"Dropped: {batchLogger.Metrics.DroppedCount}");
/// Console.WriteLine($"Flushed: {batchLogger.Metrics.TotalFlushed}");
/// Console.WriteLine($"Average batch size: {batchLogger.Metrics.AverageBatchSize:F2}");
/// </code>
/// </example>
public sealed class BatchLoggerMetrics
{
    private long _dropped;
    private long _batches;
    private long _flushed;
    private long _lastFlushTicks;

    /// <summary>
    /// Gets the total number of log entries dropped due to channel capacity limits.
    /// </summary>
    /// <remarks>
    /// When the internal channel is full, <see cref="BatchLogger"/> drops
    /// the oldest log entries to make room for new ones.
    /// This counter reflects the cumulative number of dropped entries.
    /// </remarks>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>
    /// Gets the total number of completed flush operations.
    /// </summary>
    /// <remarks>
    /// Each time <see cref="BatchLogger"/> writes a batch of log entries
    /// to its configured sinks, this counter increments by one.
    /// </remarks>
    public long BatchCount => Interlocked.Read(ref _batches);

    /// <summary>
    /// Gets the total number of log entries successfully flushed since startup.
    /// </summary>
    /// <remarks>
    /// This value can be divided by <see cref="BatchCount"/> to estimate the average batch size.
    /// </remarks>
    public long TotalFlushed => Interlocked.Read(ref _flushed);

    /// <summary>
    /// Gets the UTC timestamp of the last successful flush operation.
    /// </summary>
    /// <remarks>
    /// This value is updated whenever a batch flush completes successfully.
    /// </remarks>
    public DateTime LastFlushUtc => new(Interlocked.Read(ref _lastFlushTicks), DateTimeKind.Utc);

    /// <summary>
    /// Gets the average number of log entries flushed per batch.
    /// </summary>
    /// <remarks>
    /// Calculated as <c>TotalFlushed / BatchCount</c>.
    /// Returns <c>0</c> if no batches have been flushed yet.
    /// </remarks>
    public double AverageBatchSize => _batches == 0 ? 0 : (double)_flushed / _batches;

    /// <summary>
    /// Increments the count of dropped log entries.
    /// </summary>
    /// <remarks>
    /// This method is used internally when the logger's in-flight channel is full
    /// and an entry is discarded. It is atomic and lock-free.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void IncrementDropped() => Interlocked.Increment(ref _dropped);

    /// <summary>
    /// Adds the specified number of flushed entries and increments the batch count.
    /// </summary>
    /// <param name="count">The number of log entries flushed in the batch.</param>
    /// <remarks>
    /// This method is invoked after each successful flush to update metrics.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AddFlushed(int count)
    {
        _ = Interlocked.Add(ref _flushed, count);
        _ = Interlocked.Increment(ref _batches);
    }

    /// <summary>
    /// Updates the timestamp of the most recent successful flush.
    /// </summary>
    /// <param name="utc">The UTC time when the flush occurred.</param>
    /// <remarks>
    /// This value can be used to monitor the freshness of the last batch.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetLastFlush(DateTime utc) =>
        Interlocked.Exchange(ref _lastFlushTicks, utc.Ticks);
}