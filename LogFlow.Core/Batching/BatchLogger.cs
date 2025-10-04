using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LogFlow.Core.Batching.Model;
using LogFlow.Core.Batching.Model.Enums;
using LogFlow.Core.ExLogging;
using Microsoft.Extensions.Logging;

namespace LogFlow.Core.Batching;

/*
 * Developer ::> Gehan Fernando 
 * Date      ::> 2025-10-01
 * Contact   ::> f.gehan@gmail.com / + 46 73 701 40 25
*/

/// <summary>
/// Provides an asynchronous, batched implementation of <see cref="ILogger"/>.
/// Entries are queued via a bounded <see cref="Channel{T}"/> and flushed to one or more sinks
/// when either a size threshold (<see cref="BatchLoggerOptions.BatchSize"/>) or a time threshold
/// (<see cref="BatchLoggerOptions.FlushInterval"/>) is met.
/// </summary>
/// <remarks>
/// <para>
/// This logger is optimized for high-throughput scenarios. It minimizes contention on logging hot paths
/// by avoiding synchronous I/O and delegating work to a single background consumer.
/// </para>
/// <para>
/// Thread safety:
/// Writers enqueue entries concurrently without locks (except the channel's own concurrency).
/// The internal buffer (<see cref="_buffer"/>) is only accessed by the background worker and during flush paths
/// after cancellation/await ensures single-threaded access.
/// </para>
/// <para>
/// Disposal:
/// On <see cref="Dispose"/> or <see cref="DisposeAsync"/>, the worker is cancelled and any remaining entries
/// are flushed (respecting custom sinks if configured). Async disposal is preferred for best responsiveness.
/// </para>
/// </remarks>
public sealed class BatchLogger : ILogger, IDisposable, IAsyncDisposable
{
    // Primary downstream logger used for compatibility flushing and as a default sink.
    private readonly ILogger _sink;

    // Bounded channel that buffers log entries. Single reader (the worker), multiple writers (log callers).
    private readonly Channel<LogEntry> _channel;

    // Cancellation token source used to stop the background worker and coordinate graceful shutdown.
    private readonly CancellationTokenSource _cts = new();

    // Background task that drains the channel, batches entries, and triggers flushes.
    private readonly Task _worker;

    // NOTE: _buffer is only touched by the background worker and flush methods
    // after the worker has been cancelled/awaited. It is NOT thread-safe by design.
    private readonly List<LogEntry> _buffer;

    // Effective options for this logger instance.
    private readonly BatchLoggerOptions _options;

    // When OnFlushAsync is not provided, we may build a composite sink from options.
    private readonly Func<IReadOnlyList<BatchLogEntry>, CancellationToken, Task> _composedFlushAsync;

    /// <summary>
    /// Exposes counters and timestamps such as dropped entry count and last flush time.
    /// Useful for health metrics and diagnostics dashboards.
    /// </summary>
    public BatchLoggerMetrics Metrics { get; } = new();

    /// <summary>
    /// Exception formatter used by <c>ExLog*Exception</c> helpers.
    /// Defaults to <see cref="ExLogger.ExceptionFormatter"/>.
    /// </summary>
    public Func<Exception, string, bool, string> ExceptionFormatter { get; set; } = ExLogger.ExceptionFormatter;

    /// <summary>
    /// Initializes a new instance of the <see cref="BatchLogger"/> class.
    /// </summary>
    /// <param name="sink">Underlying <see cref="ILogger"/> used as a fallback and/or composed sink.</param>
    /// <param name="options">Optional configuration; if null, sensible defaults are used.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="sink"/> is null.</exception>
    public BatchLogger(ILogger sink, BatchLoggerOptions options = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _options = options ?? new BatchLoggerOptions();
        _buffer = new List<LogEntry>(_options.BatchSize);

        // Create a bounded channel with backpressure; when full, drop oldest to avoid blocking the app.
        _channel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(_options.Capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        // If the caller didn't provide OnFlushAsync but enabled file/database options,
        // build a composed async flush pipeline.
        _composedFlushAsync = BuildComposedFlushIfNeeded(_options);

        // Start the single-reader background worker that processes the queue.
        _worker = Task.Run(() => ProcessAsync(_cts.Token), _cts.Token);
    }

    // ---------------- ILogger ----------------

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => _sink.IsEnabled(logLevel);

    /// <inheritdoc />
    public IDisposable BeginScope<TState>(TState state)
        // Preserve any underlying scope if available; else return a no-op scope to avoid null checks.
        => _sink.BeginScope(state) ?? NullScope.Instance;

    /// <inheritdoc />
    /// <remarks>
    /// This method performs minimal work on the caller thread:
    /// it evaluates the formatter (if present), runs an optional filter, and enqueues the entry.
    /// If the channel is full, the oldest entry is dropped (configured in channel options).
    /// </remarks>
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
        Func<TState, Exception, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return; // Fast path: disabled level
        }

        // Build the message and extract structured logging args if present.
        var msg = formatter != null ? formatter(state, exception) : state?.ToString() ?? "N/A";
        var args = state is IEnumerable<KeyValuePair<string, object>> kv
            ? [.. kv.Select(k => k.Value ?? "N/A")]
            : Array.Empty<object>();

        // Optional filter: if provided by options, allow the caller to short-circuit logging.
        if (_options.Filter != null && !_options.Filter(logLevel, msg, exception, args))
        {
            return;
        }

        // Enqueue to the bounded channel. If it fails (buffer full), increment drop metric.
        Enqueue(new LogEntry(logLevel, msg, exception, args));
    }

    // ---------------- ExLogger-style Methods ----------------

    /// <summary>Logs at <see cref="LogLevel.Trace"/>.</summary>
    public void ExLogTrace(string msg, params object[] args) => LogInternal(LogLevel.Trace, msg, null, args);

    /// <summary>Logs at <see cref="LogLevel.Debug"/>.</summary>
    public void ExLogDebug(string msg, params object[] args) => LogInternal(LogLevel.Debug, msg, null, args);

    /// <summary>Logs at <see cref="LogLevel.Information"/>.</summary>
    public void ExLogInformation(string msg, params object[] args) => LogInternal(LogLevel.Information, msg, null, args);

    /// <summary>Logs at <see cref="LogLevel.Warning"/>.</summary>
    public void ExLogWarning(string msg, params object[] args) => LogInternal(LogLevel.Warning, msg, null, args);

    /// <summary>Logs at <see cref="LogLevel.Error"/> with a message.</summary>
    public void ExLogError(string msg, params object[] args) => LogInternal(LogLevel.Error, msg, null, args);

    /// <summary>Logs at <see cref="LogLevel.Error"/> with an exception.</summary>
    public void ExLogError(string msg, Exception ex, params object[] args) => LogInternal(LogLevel.Error, msg, ex, args);

    /// <summary>Logs at <see cref="LogLevel.Critical"/> with a message.</summary>
    public void ExLogCritical(string msg, params object[] args) => LogInternal(LogLevel.Critical, msg, null, args);

    /// <summary>Logs at <see cref="LogLevel.Critical"/> with an exception.</summary>
    public void ExLogCritical(string msg, Exception ex, params object[] args) => LogInternal(LogLevel.Critical, msg, ex, args);

    /// <summary>
    /// Formats and logs an exception at <see cref="LogLevel.Error"/> using <see cref="ExceptionFormatter"/>.
    /// </summary>
    /// <param name="ex">Exception instance to log.</param>
    /// <param name="title">Short human-readable title for the exception.</param>
    /// <param name="details">If true, include extended details in the formatted message.</param>
    public void ExLogErrorException(Exception ex, string title = "System Error", bool details = false)
    {
        if (!IsEnabled(LogLevel.Error))
        {
            return;
        }

        // Format message with the provided or default exception formatter.
        var msg = (ExceptionFormatter ?? ExLogger.ExceptionFormatter)(ex, title, details);

        // Respect optional filter.
        if (_options.Filter != null && !_options.Filter(LogLevel.Error, msg, ex, Array.Empty<object>()))
        {
            return;
        }

        LogInternal(LogLevel.Error, msg, ex);
    }

    /// <summary>
    /// Formats and logs an exception at <see cref="LogLevel.Critical"/> using <see cref="ExceptionFormatter"/>.
    /// </summary>
    /// <param name="ex">Exception instance to log.</param>
    /// <param name="title">Short human-readable title for the exception.</param>
    /// <param name="details">If true, include extended details in the formatted message.</param>
    public void ExLogCriticalException(Exception ex, string title = "Critical System Error", bool details = false)
    {
        if (!IsEnabled(LogLevel.Critical))
        {
            return;
        }

        var msg = (ExceptionFormatter ?? ExLogger.ExceptionFormatter)(ex, title, details);

        // Respect optional filter.
        if (_options.Filter != null && !_options.Filter(LogLevel.Critical, msg, ex, Array.Empty<object>()))
        {
            return;
        }

        LogInternal(LogLevel.Critical, msg, ex);
    }

    // ---------------- Internal Core ----------------

    /// <summary>
    /// Common internal logging path for the ExLogger-style helpers.
    /// Applies filtering and enqueues a <see cref="LogEntry"/>.
    /// </summary>
    private void LogInternal(LogLevel lvl, string msg, Exception ex, params object[] args)
    {
        if (!IsEnabled(lvl))
        {
            return;
        }

        // Respect optional filter across all helper-based calls.
        if (_options.Filter != null && !_options.Filter(lvl, msg ?? "N/A", ex, args))
        {
            return;
        }

        var entry = new LogEntry(lvl, msg ?? "N/A", ex, args ?? Array.Empty<object>());
        Enqueue(entry);
    }

    /// <summary>
    /// Attempts to write a log entry into the channel; if the channel is full, increments the dropped count.
    /// </summary>
    private void Enqueue(LogEntry entry)
    {
        if (!_channel.Writer.TryWrite(entry))
        {
            Metrics.IncrementDropped();
        }
    }

    /// <summary>
    /// Background worker loop that waits on either new data or the flush interval, drains the channel,
    /// and flushes the accumulated batch when size or time thresholds are met.
    /// </summary>
    private async Task ProcessAsync(CancellationToken token)
    {
        var reader = _channel.Reader;
        var flushInterval = _options.FlushInterval;
        var batchSize = _options.BatchSize;

        try
        {
            while (!token.IsCancellationRequested)
            {
                // Race between data availability and timer. Whichever completes first triggers a pass.
                var delayTask = Task.Delay(flushInterval, token);
                var readTask = reader.WaitToReadAsync(token).AsTask();
                var completed = await Task.WhenAny(readTask, delayTask).ConfigureAwait(false);

                // Drain any available items immediately.
                while (reader.TryRead(out var entry))
                {
                    _buffer.Add(entry);

                    // If we hit the batch size threshold, flush immediately to reduce memory pressure.
                    if (_buffer.Count >= batchSize)
                    {
                        await FlushAsync(token).ConfigureAwait(false);
                    }
                }

                // If the time-based trigger fired and we have items, flush them.
                if (_buffer.Count > 0 && completed == delayTask)
                {
                    await FlushAsync(token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown; no action needed.
        }
        catch (Exception ex)
        {
            // Log via the underlying sink and also surface through the optional internal error callback.
            try
            {
                ExLogger.Log(_sink, LogLevel.Error, "BatchLogger background failure", ex);
            }
            catch
            {
                // Swallow if the sink itself is failing.
            }

            try
            {
                _options.OnInternalError?.Invoke("BatchLogger background failure", ex);
            }
            catch
            {
                // Never allow diagnostics to crash the worker.
            }
        }

        // Final flush attempt after loop exits.
        if (_buffer.Count > 0)
        {
            await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Flushes a concrete buffer (direct to <see cref="_sink"/>). Used when no custom async sink is present.
    /// </summary>
    /// <param name="buf">Buffer to flush. This buffer will be cleared on exit.</param>
    private void Flush(List<LogEntry> buf)
    {
        if (buf.Count == 0)
        {
            return;
        }

        try
        {
            foreach (var e in buf)
            {
                try
                {
                    // Use the ExLogger helper to preserve formatting behavior.
                    ExLogger.Log(_sink, e.Level, e.Message, e.Exception, e.Args);
                }
                catch (Exception ex)
                {
                    // If individual sink write fails, record a drop and surface an internal error.
                    Metrics.IncrementDropped();
                    System.Diagnostics.Debug.WriteLine($"[BatchLogger] Flush error: {ex}");
                    try
                    { _options.OnInternalError?.Invoke("BatchLogger flush error", ex); }
                    catch { /* ignore */ }
                }
            }

            // Update metrics on success.
            Metrics.AddFlushed(buf.Count);
            Metrics.SetLastFlush(DateTime.UtcNow);
        }
        finally
        {
            buf.Clear(); // Always clear to free memory and reset state.
        }
    }

    /// <summary>
    /// Public async flush. If a custom sink handler is configured (via <see cref="BatchLoggerOptions.OnFlushAsync"/>
    /// or built-in composition), it is used; otherwise, the flush falls back to the underlying <see cref="_sink"/>.
    /// </summary>
    /// <param name="token">Cancellation token to cooperatively cancel the flush.</param>
    public async Task FlushAsync(CancellationToken token = default)
    {
        // Drain whatever is currently in the channel to make this flush as complete as possible.
        while (_channel.Reader.TryRead(out var e))
        {
            token.ThrowIfCancellationRequested();
            _buffer.Add(e);

            // Optionally chunk large buffers when using custom sinks to reduce per-call payload size.
            if (_buffer.Count >= _options.BatchSize)
            {
                if (_options.OnFlushAsync is not null || _composedFlushAsync is not null)
                {
                    await FlushViaCustomSinkAsync(token).ConfigureAwait(false);
                }
                else
                {
                    Flush(_buffer);
                }
            }
        }

        if (_buffer.Count == 0)
        {
            // Keep method truly async to avoid sync-over-async surprises in callers.
            await Task.Yield();
            return;
        }

        // Choose custom sink pipeline if configured; else default to direct ILogger.
        if (_options.OnFlushAsync is not null || _composedFlushAsync is not null)
        {
            await FlushViaCustomSinkAsync(token).ConfigureAwait(false);
        }
        else
        {
            Flush(_buffer);
        }

        await Task.Yield();
    }

    /// <summary>
    /// Flushes the in-memory buffer using the configured custom sink delegate or composed sinks.
    /// Falls back to direct sink flush upon exceptions to reduce data loss.
    /// </summary>
    private async Task FlushViaCustomSinkAsync(CancellationToken token)
    {
        if (_buffer.Count == 0)
        {
            return;
        }

        // Build a stable snapshot in the public-facing entry shape.
        List<BatchLogEntry> snapshot = new(_buffer.Count);
        foreach (var e in _buffer)
        {
            snapshot.Add(new BatchLogEntry(e.Level, e.Message, e.Exception, e.Args));
        }

        try
        {
            if (_options.OnFlushAsync is not null)
            {
                await _options.OnFlushAsync(snapshot, token).ConfigureAwait(false);
            }
            else if (_composedFlushAsync is not null)
            {
                await _composedFlushAsync(snapshot, token).ConfigureAwait(false);
            }
            else
            {
                // Should never happen, but if it does, use direct sink flush.
                Flush(_buffer);
                return;
            }

            // Mirror metrics behavior of the direct flush path.
            Metrics.AddFlushed(snapshot.Count);
            Metrics.SetLastFlush(DateTime.UtcNow);
        }
        catch (OperationCanceledException)
        {
            // Cooperatively propagate cancellation.
            throw;
        }
        catch (Exception ex)
        {
            // Notify the optional internal error handler and attempt a best-effort fallback flush.
            try
            {
                _options.OnInternalError?.Invoke("BatchLogger OnFlushAsync handler error", ex);
            }
            catch
            {
                // Swallow exceptions from the error handler itself.
            }

            // Fallback to the synchronous underlying sink flush to avoid losing data.
            await Task.Run(() => Flush(_buffer), token).ConfigureAwait(false);
        }
        finally
        {
            _buffer.Clear();
        }
    }

#if DEBUG
    /// <summary>
    /// Test helper that forces an immediate flush without requiring cancellation of the worker.
    /// </summary>
    internal Task ForceFlushAsync(CancellationToken token = default) => FlushAsync(token);
#endif

    // ---------------- IDisposable ----------------

    /// <summary>
    /// Synchronous dispose. Cancels the worker, drains the channel, and flushes remaining entries.
    /// If a custom async sink is configured, we attempt to flush via that sink; on failure we fallback
    /// to direct sink flush to minimize data loss.
    /// </summary>
    public void Dispose()
    {
        try
        {
            // Stop accepting new writes and cancel the worker.
            _ = _channel.Writer.TryComplete();
            _cts.Cancel();

            try
            {
                // Block until the worker finishes its loop.
                _worker.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is cooperative.
            }

            // Drain any remaining items that may have been enqueued after the last read pass.
            while (_channel.Reader.TryRead(out var e))
            {
                _buffer.Add(e);
            }

            // If there is data left, flush it now.
            if (_buffer.Count > 0)
            {
                if (_options.OnFlushAsync is not null || _composedFlushAsync is not null)
                {
                    try
                    {
                        // Try custom sinks first, synchronously waited.
                        FlushViaCustomSinkAsync(CancellationToken.None).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        // Notify and fallback to direct sink flush.
                        try
                        { _options.OnInternalError?.Invoke("BatchLogger Dispose flush error", ex); }
                        catch { /* ignore */ }
                        Flush(_buffer);
                    }
                }
                else
                {
                    Flush(_buffer);
                }
            }
        }
        finally
        {
            _cts.Dispose();
        }
    }

    /// <summary>
    /// Asynchronous dispose. Cancels the worker using <see cref="CancellationTokenSource.CancelAsync"/>,
    /// awaits the worker completion, and performs an asynchronous flush of remaining entries.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            _ = _channel.Writer.TryComplete();

            // Async-friendly cancellation in modern runtimes.
            await _cts.CancelAsync().ConfigureAwait(false);

            try
            {
                // Await the worker so we know the reader has stopped.
                await _worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is cooperative.
            }

            // Finally flush whatever remains using the configured path.
            await FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _cts.Dispose();
        }
    }

    // ---------------- Internal record ----------------

    /// <summary>
    /// Internal immutable log entry used for fast, allocation-friendly queuing.
    /// </summary>
    private readonly record struct LogEntry(LogLevel Level, string Message, Exception Exception, object[] Args);

    // ---------------- Sink composition ----------------

    /// <summary>
    /// Builds a composed async flush delegate when <see cref="BatchLoggerOptions.OnFlushAsync"/> is not provided
    /// but file and/or database sinks are enabled. Optionally includes forwarding to <see cref="ILogger"/> behavior.
    /// Returns <c>null</c> if no composition is needed.
    /// </summary>
    private static Func<IReadOnlyList<BatchLogEntry>, CancellationToken, Task> BuildComposedFlushIfNeeded(BatchLoggerOptions options)
    {
        // If the user provided a custom handler, no composition is required.
        if (options.OnFlushAsync is not null)
        {
            return null;
        }

        var activeSinks = new List<IBatchSink>(capacity: 4);

        if (options.File?.Enabled == true)
        {
            activeSinks.Add(new FileBatchSink(options.File, options.Format));
        }

        if (options.Database?.Enabled == true)
        {
            activeSinks.Add(new DatabaseBatchSink(options.Database, options.Format));
        }

        // Retain the "forward to ILogger" behavior by including a no-op sink placeholder.
        if (options.ForwardToILoggerSink)
        {
            activeSinks.Add(new LoggerBatchSink());
        }

        if (activeSinks.Count == 0)
        {
            return null; // Nothing to compose.
        }

        // Compose: attempt all sinks; surface errors via OnInternalError but do not throw.
        return async (entries, token) =>
        {
            List<Exception> errors = [];

            foreach (var sink in activeSinks)
            {
                try
                {
                    await sink.WriteAsync(entries, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw; // Preserve cooperative cancellation.
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                    try
                    { options.OnInternalError?.Invoke($"{sink.GetType().Name} error", ex); }
                    catch { /* ignore secondary errors */ }
                }
            }

            // If any sink failed, we already notified via OnInternalError. Best-effort write was attempted.
        };
    }

    // -------------- Internal sink types ----------------

    /// <summary>
    /// Simple sink abstraction for batch-oriented writes.
    /// </summary>
    private interface IBatchSink
    {
        /// <summary>
        /// Writes a batch of log entries to the target sink.
        /// </summary>
        /// <param name="entries">The entries to write.</param>
        /// <param name="token">Cancellation token.</param>
        Task WriteAsync(IReadOnlyList<BatchLogEntry> entries, CancellationToken token);
    }

    /// <summary>
    /// Placeholder sink representing forwarding to the underlying <see cref="ILogger"/>.
    /// The actual write is performed by the caller's fallback to <see cref="Flush(List{LogEntry})"/>.
    /// </summary>
    private sealed class LoggerBatchSink : IBatchSink
    {
        /// <inheritdoc />
        public Task WriteAsync(IReadOnlyList<BatchLogEntry> entries, CancellationToken token) =>
            // No-op: the parent logger handles forwarding to ILogger when this is the only sink.
            Task.CompletedTask;
    }

    /// <summary>
    /// Rolling file batch sink with minimal dependencies.
    /// Supports rolling by interval and size, as well as JSON and plain-text formats.
    /// </summary>
    private sealed class FileBatchSink : IBatchSink
    {
        private readonly BatchFileOptions _opts;
        private readonly BatchFormatOptions _fmt;

        /// <summary>
        /// Creates a new file batch sink.
        /// </summary>
        /// <param name="opts">File rolling and path options.</param>
        /// <param name="fmt">Formatting options for JSON and args serialization.</param>
        public FileBatchSink(BatchFileOptions opts, BatchFormatOptions fmt)
        {
            _opts = opts;
            _fmt = fmt ?? new BatchFormatOptions();

            // Ensure the directory exists before writing.
            _ = Directory.CreateDirectory(Path.GetDirectoryName(GetCurrentPath())!);
        }

        /// <inheritdoc />
        public async Task WriteAsync(IReadOnlyList<BatchLogEntry> entries, CancellationToken token)
        {
            var path = GetCurrentPath();

            // Roll by size when configured and the active file exceeds the threshold.
            if (File.Exists(path) && _opts.RollingSizeBytes > 0)
            {
                var len = new FileInfo(path).Length;
                if (len >= _opts.RollingSizeBytes)
                {
                    RollFiles();
                }
            }

            // Append mode with async file I/O; share read to allow tailing.
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, useAsync: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            // Write each entry as a single line using the requested format.
            foreach (var e in entries)
            {
                token.ThrowIfCancellationRequested();
                var line = Format(e, _fmt, _opts.Format);
                await writer.WriteLineAsync(line.AsMemory(), token).ConfigureAwait(false);
            }

            await writer.FlushAsync(token).ConfigureAwait(false);
        }

        // Computes the current file path based on rolling interval.
        private string GetCurrentPath()
        {
            if (_opts.RollingInterval == RollingInterval.None)
            {
                return _opts.Path;
            }

            var stamp = DateTime.UtcNow;
            var suffix = _opts.RollingInterval switch
            {
                RollingInterval.Hour => stamp.ToString("yyyyMMdd_HH", CultureInfo.InvariantCulture),
                RollingInterval.Day => stamp.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                RollingInterval.Week => $"{stamp:yyyyMMdd}-W{CultureInfo.InvariantCulture.Calendar.GetWeekOfYear(stamp, CalendarWeekRule.FirstDay, DayOfWeek.Monday)}",
                RollingInterval.Month => stamp.ToString("yyyyMM", CultureInfo.InvariantCulture),
                RollingInterval.Year => stamp.ToString("yyyy", CultureInfo.InvariantCulture),
                _ => stamp.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            };

            var dir = Path.GetDirectoryName(_opts.Path)!;
            var file = Path.GetFileNameWithoutExtension(_opts.Path);
            var ext = Path.GetExtension(_opts.Path);
            return Path.Combine(dir, $"{file}-{suffix}{ext}");
        }

        // Rolls files by moving suffixes .N to .N+1 and current to .1; keeps RetainedFileCountLimit files.
        private void RollFiles()
        {
            var path = GetCurrentPath();
            if (!File.Exists(path))
            {
                return;
            }

            static void SafeMove(string src, string dst)
            {
                try
                {
                    if (File.Exists(src))
                    {
                        File.Move(src, dst, overwrite: true);
                    }
                }
                catch
                {
                    // Ignore rolling failures to avoid blocking logging.
                }
            }

            for (var i = _opts.RetainedFileCountLimit - 1; i >= 1; i--)
            {
                var older = $"{path}.{i}";
                var newer = $"{path}.{i + 1}";
                SafeMove(older, newer);
            }

            SafeMove(path, $"{path}.1");
        }

        // Formats a single entry according to the configured file format and serialization options.
        private static string Format(BatchLogEntry e, BatchFormatOptions fmt, BatchFileFormat fileFormat)
        {
            if (fileFormat == BatchFileFormat.Json)
            {
                var payload = new
                {
                    tsUtc = DateTime.UtcNow,
                    level = e.Level.ToString(),
                    message = e.Message,
                    exception = e.Exception?.ToString(),
                    args = e.Args,
                };

                return JsonSerializer.Serialize(payload, fmt.JsonSerializerOptions ?? new JsonSerializerOptions { WriteIndented = false });
            }

            // Plain text format: "{TimestampUtc:O} [Level] Message | args=<json> | ex=<exception>"
            var sb = new StringBuilder(256);
            _ = sb.Append(DateTime.UtcNow.ToString("O"))
                  .Append(" [").Append(e.Level).Append("] ")
                  .Append(e.Message ?? "N/A");

            if (e.Args is { Count: > 0 })
            {
                _ = sb.Append(" | args=").Append(JsonSerializer.Serialize(e.Args, fmt.JsonSerializerOptions ?? new()));
            }

            if (e.Exception is not null)
            {
                _ = sb.Append(" | ex=").Append(e.Exception);
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// Minimal ADO.NET batch inserter that works with providers registered via <see cref="DbProviderFactories"/>.
    /// Creates a connection per flush and inserts rows using parameterized commands.
    /// Optionally creates the log table if <see cref="BatchDatabaseOptions.AutoCreateTable"/> is set.
    /// </summary>
    private sealed class DatabaseBatchSink : IBatchSink
    {
        private readonly BatchDatabaseOptions _opts;
        private readonly BatchFormatOptions _fmt;

        /// <summary>
        /// Initializes a new instance of the <see cref="DatabaseBatchSink"/> class.
        /// </summary>
        /// <param name="opts">Database connection and schema options.</param>
        /// <param name="fmt">JSON formatting options for args column.</param>
        /// <exception cref="ArgumentException">Thrown if required database options are missing.</exception>
        public DatabaseBatchSink(BatchDatabaseOptions opts, BatchFormatOptions fmt)
        {
            _opts = opts;
            _fmt = fmt ?? new BatchFormatOptions();

            if (string.IsNullOrWhiteSpace(_opts.ProviderInvariantName))
            {
                throw new ArgumentException("Database.ProviderInvariantName is required.");
            }

            if (string.IsNullOrWhiteSpace(_opts.ConnectionString))
            {
                throw new ArgumentException("Database.ConnectionString is required.");
            }
        }

        /// <inheritdoc />
        public async Task WriteAsync(IReadOnlyList<BatchLogEntry> entries, CancellationToken token)
        {
            var factory = DbProviderFactories.GetFactory(_opts.ProviderInvariantName);

            await using var conn = factory.CreateConnection()!;
            conn.ConnectionString = _opts.ConnectionString;
            await conn.OpenAsync(token).ConfigureAwait(false);

            // Optionally create the table. Failures are ignored (permissions/races).
            if (_opts.AutoCreateTable)
            {
                try
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = _opts.GetCreateTableSql();
                    _ = await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore table creation errors (another instance may have created it).
                }
            }

            // Insert each entry using parameterized commands (portable across providers).
            var insertSql = _opts.GetInsertSql();

            foreach (var e in entries)
            {
                token.ThrowIfCancellationRequested();

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = insertSql;

                // Timestamp (UTC)
                var pTs = cmd.CreateParameter();
                pTs.ParameterName = _opts.ColTimestamp;
                pTs.Value = DateTime.UtcNow;
                _ = cmd.Parameters.Add(pTs);

                // Level (string)
                var pLvl = cmd.CreateParameter();
                pLvl.ParameterName = _opts.ColLevel;
                pLvl.Value = e.Level.ToString();
                _ = cmd.Parameters.Add(pLvl);

                // Message
                var pMsg = cmd.CreateParameter();
                pMsg.ParameterName = _opts.ColMessage;
                pMsg.Value = e.Message ?? "";
                _ = cmd.Parameters.Add(pMsg);

                // Exception (nullable)
                var pExc = cmd.CreateParameter();
                pExc.ParameterName = _opts.ColException;
                pExc.Value = e.Exception?.ToString() ?? (object)DBNull.Value;
                _ = cmd.Parameters.Add(pExc);

                // Args as JSON (nullable)
                var pArgs = cmd.CreateParameter();
                pArgs.ParameterName = _opts.ColArgsJson;
                pArgs.Value = (e.Args is { Count: > 0 })
                    ? JsonSerializer.Serialize(e.Args, _fmt.JsonSerializerOptions ?? new())
                    : DBNull.Value;
                _ = cmd.Parameters.Add(pArgs);

                _ = await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }
    }
}